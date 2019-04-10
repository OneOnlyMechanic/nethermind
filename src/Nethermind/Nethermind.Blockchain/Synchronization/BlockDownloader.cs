using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Mining;

namespace Nethermind.Blockchain.Synchronization
{
    internal class BlockDownloader
    {
        public const int MaxReorganizationLength = 2 * SyncBatchSize.Max;
        
        private readonly IBlockTree _blockTree;
        private readonly IBlockValidator _blockValidator;
        private readonly ISealValidator _sealValidator;
        private readonly ILogger _logger;

        private SynchronizationStats _syncStats;
        private SyncBatchSize _syncBatchSize;
        private int _sinceLastTimeout;

        public BlockDownloader(IBlockTree blockTree, IBlockValidator blockValidator, ISealValidator sealValidator, ILogManager logManager)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _sealValidator = sealValidator ?? throw new ArgumentNullException(nameof(sealValidator));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            
            _syncBatchSize = new SyncBatchSize(logManager);
            _syncStats = new SynchronizationStats(logManager);
        }

        public async Task<int> DownloadHeaders(PeerInfo bestPeer, CancellationToken cancellation)
        {
            if (bestPeer == null)
            {
                throw new ArgumentNullException($"Not expecting best peer to be null inside the {nameof(BlockDownloader)}");
            }
            
            int headersSynced = 0;
            int ancestorLookupLevel = 0;
            int emptyHeadersListCounter = 0;

            long currentNumber = Math.Min(_blockTree.BestKnownNumber, bestPeer.HeadNumber - 1);
            while (bestPeer.TotalDifficulty > (_blockTree.BestSuggested?.TotalDifficulty ?? 0) && currentNumber <= bestPeer.HeadNumber)
            {
                if (_logger.IsTrace) _logger.Trace($"Continue headers sync with {bestPeer} (our best {_blockTree.BestKnownNumber})");

                if (ancestorLookupLevel > MaxReorganizationLength)
                {
                    if (_logger.IsWarn) _logger.Warn($"Could not find common ancestor with {bestPeer}");
                    throw new EthSynchronizationException("Peer with inconsistent chain in sync");
                }

                long blocksLeft = bestPeer.HeadNumber - currentNumber - SyncModeSelector.FullSyncThreshold;
                int headersToRequest = (int) BigInteger.Min(blocksLeft + 1, _syncBatchSize.Current);
                if (headersToRequest <= 1)
                {
                    break;
                }

                if (_logger.IsTrace) _logger.Trace($"Headers request {currentNumber}+{headersToRequest} to peer {bestPeer.SyncPeer.Node.Id} with {bestPeer.HeadNumber} blocks. Got {currentNumber} and asking for {headersToRequest} more.");
                var headers = await RequestHeaders(bestPeer, cancellation, currentNumber, headersToRequest);

                int nonEmptyHeadersCount = 0;
                for (int i = 1; i < headers.Length; i++)
                {
                    if (headers[i] != null)
                    {
                        nonEmptyHeadersCount++;
                    }
                    else
                    {
                        break;
                    }
                }

                if (_logger.IsTrace) _logger.Trace($"Actual batch size was {nonEmptyHeadersCount + 1}/{_syncBatchSize.Current}");
                if (nonEmptyHeadersCount == 0)
                {
                    if (headers.Length == 1)
                    {
                        // for some reasons we take current number as peerInfo.HeadNumber - 1 (I do not remember why)
                        // and also there may be a race in total difficulty measurement
                        break;
                    }

                    if (++emptyHeadersListCounter >= 10)
                    {
                        if (_syncBatchSize.Current == SyncBatchSize.Min)
                        {
                            if (_logger.IsInfo) _logger.Info($"Received no blocks from {bestPeer} in response to {headersToRequest} blocks requested. Cancelling.");
                            throw new EthSynchronizationException("Peer sent an empty header list");
                        }

                        if (_logger.IsInfo) _logger.Info($"Received no blocks from {bestPeer} in response to {headersToRequest} blocks requested.");
                        _syncBatchSize.Shrink();
                    }

                    continue;
                }


                if (_logger.IsTrace) _logger.Trace($"Non-empty headers length is {nonEmptyHeadersCount}, counter is {emptyHeadersListCounter}");
                emptyHeadersListCounter = 0;
                _sinceLastTimeout++;
                if (_sinceLastTimeout >= 2)
                {
                    _syncBatchSize.Expand();
                }

                BlockHeader parent = headers[1] == null ? null : _blockTree.FindParentHeader(headers[1]);
                if (parent == null)
                {
                    ancestorLookupLevel += _syncBatchSize.Current;
                    currentNumber = currentNumber >= _syncBatchSize.Current ? (currentNumber - _syncBatchSize.Current) : 0L;
                    continue;
                }

                for (int i = 1; i < nonEmptyHeadersCount + 1; i++)
                {
                    BlockHeader currentHeader = headers[i];
                    if (cancellation.IsCancellationRequested)
                    {
                        if (_logger.IsTrace) _logger.Trace("Peer fast sync cancelled");
                        break;
                    }

                    if (_logger.IsTrace) _logger.Trace($"Received {currentHeader} from {bestPeer.SyncPeer.Node:s}");
                    if (!_blockValidator.ValidateHeader(currentHeader, false))
                    {
                        if (_logger.IsWarn) _logger.Warn($"Block {currentHeader.Number} skipped (validation failed)");
                        continue;
                    }

                    if (HandleAddResult(currentHeader, i == 0, _blockTree.SuggestHeader(currentHeader)))
                    {
                        headersSynced++;
                    }

                    currentNumber = currentNumber + i;
                }

                _syncStats.Report(_blockTree.BestSuggested?.Number ?? 0, bestPeer.HeadNumber);
            }

            return headersSynced;
        }

        public async Task<int> DownloadBlocks(PeerInfo bestPeer, CancellationToken cancellation)
        {
            if (bestPeer == null)
            {
                throw new ArgumentNullException($"Not expecting best peer to be null inside the {nameof(BlockDownloader)}");
            }
            
            int blocksSynced = 0;
            int ancestorLookupLevel = 0;
            int emptyBlockListCounter = 0;

            long currentNumber = Math.Min(_blockTree.BestKnownNumber, bestPeer.HeadNumber - 1);
            while (bestPeer.TotalDifficulty > (_blockTree.BestSuggested?.TotalDifficulty ?? 0) && currentNumber <= bestPeer.HeadNumber)
            {
                if (_logger.IsDebug) _logger.Debug($"Continue full sync with {bestPeer} (our best {_blockTree.BestKnownNumber})");
                if (ancestorLookupLevel > MaxReorganizationLength)
                {
                    if (_logger.IsWarn) _logger.Warn($"Could not find common ancestor with {bestPeer}");
                    throw new EthSynchronizationException("Peer with inconsistent chain in sync");
                }

                long blocksLeft = bestPeer.HeadNumber - currentNumber;
                int blocksToRequest = (int) BigInteger.Min(blocksLeft + 1, _syncBatchSize.Current);
                if (blocksToRequest <= 1)
                {
                    break;
                }
                
                if (_logger.IsTrace) _logger.Trace($"Full sync request {currentNumber}+{blocksToRequest} to peer {bestPeer.SyncPeer.Node.Id} with {bestPeer.HeadNumber} blocks. Got {currentNumber} and asking for {blocksToRequest} more.");
                var headers = await RequestHeaders(bestPeer, cancellation, currentNumber, blocksToRequest);

                List<Keccak> hashes = new List<Keccak>();
                Dictionary<Keccak, BlockHeader> headersByHash = new Dictionary<Keccak, BlockHeader>();
                for (int i = 1; i < headers.Length; i++)
                {
                    if (headers[i] == null)
                    {
                        break;
                    }

                    hashes.Add(headers[i].Hash);
                    headersByHash[headers[i].Hash] = headers[i];
                }

                if (hashes.Count == 0)
                {
                    if (headers.Length == 1)
                    {
                        // for some reasons we take current number as peerInfo.HeadNumber - 1 (I do not remember why)
                        // and also there may be a race in total difficulty measurement
                        break;
                    }

                    throw new EthSynchronizationException("Peer sent an empty header list");
                }

                Task<Block[]> bodiesTask = bestPeer.SyncPeer.GetBlocks(hashes.ToArray(), cancellation);
                Block[] blocks = await bodiesTask;
                if (bodiesTask.IsCanceled)
                {
                    if (_logger.IsTrace) _logger.Trace("Bodies task cancelled");
                    break;
                }

                if (bodiesTask.IsFaulted)
                {
                    _sinceLastTimeout = 0;
                    if (bodiesTask.Exception?.InnerExceptions.Any(x => x.InnerException is TimeoutException) ?? false)
                    {
                        if (_logger.IsTrace) _logger.Error("Failed to retrieve bodies when synchronizing (Timeout)", bodiesTask.Exception);
                    }
                    else
                    {
                        if (_logger.IsError) _logger.Error("Failed to retrieve bodies when synchronizing", bodiesTask.Exception);
                    }

                    throw new EthSynchronizationException("Bodies task faulted.", bodiesTask.Exception);
                }

                if (blocks.Length == 0 && blocksLeft == 1)
                {
                    if (_logger.IsDebug) _logger.Debug($"{bestPeer} does not have block body for {hashes[0]}");
                }

                if (blocks.Length == 0 && ++emptyBlockListCounter >= 10)
                {
                    if (_syncBatchSize.IsMin)
                    {
                        if (_logger.IsInfo) _logger.Info($"Received no blocks from {bestPeer} in response to {blocksToRequest} blocks requested. Cancelling.");
                        throw new EthSynchronizationException("Peer sent an empty block list");
                    }

                    if (_logger.IsInfo) _logger.Info($"Received no blocks from {bestPeer} in response to {blocksToRequest} blocks requested.");
                    _syncBatchSize.Shrink();
                    continue;
                }

                if (blocks.Length != 0)
                {
                    if (_logger.IsTrace) _logger.Trace($"Blocks length is {blocks.Length}, counter is {emptyBlockListCounter}");
                    emptyBlockListCounter = 0;
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace($"Blocks length is 0, counter is {emptyBlockListCounter}");
                    continue;
                }

                _sinceLastTimeout++;
                if (_sinceLastTimeout > 8)
                {
                    _syncBatchSize.Expand();
                }

                for (int i = 0; i < blocks.Length; i++)
                {
                    blocks[i].Header = headersByHash[hashes[i]];
                }

                if (blocks.Length > 0)
                {
                    BlockHeader parent = _blockTree.FindParentHeader(blocks[0].Header);
                    if (parent == null)
                    {
                        ancestorLookupLevel += _syncBatchSize.Current;
                        currentNumber = currentNumber >= _syncBatchSize.Current ? (currentNumber - _syncBatchSize.Current) : 0L;
                        continue;
                    }
                }

                for (int i = 0; i < blocks.Length; i++)
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        if (_logger.IsTrace) _logger.Trace("Peer sync cancelled");
                        return blocksSynced;
                    }

                    if (_logger.IsTrace) _logger.Trace($"Received {blocks[i]} from {bestPeer}");

                    // can move this to block tree now?
                    if (!_blockValidator.ValidateSuggestedBlock(blocks[i]))
                    {
                        if (_logger.IsWarn) _logger.Warn($"Block {blocks[i].Number} skipped (validation failed)");
                        continue;
                    }

                    HandleAddResult(blocks[i].Header, i == 0, _blockTree.SuggestBlock(blocks[i]));
                    
                    currentNumber = currentNumber + i;
                }

                _syncStats.Report(_blockTree.BestSuggested?.Number ?? 0, bestPeer.HeadNumber);
            }
            
            return blocksSynced;
        }

        private async Task<BlockHeader[]> RequestHeaders(PeerInfo bestPeer, CancellationToken cancellation, long currentNumber, int headersToRequest)
        {
            Task<BlockHeader[]> headersTask = bestPeer.SyncPeer.GetBlockHeaders(currentNumber, headersToRequest, 0, cancellation);
            BlockHeader[] headers = await headersTask;
            if (headersTask.IsCanceled)
            {
                if (_logger.IsTrace) _logger.Trace("Headers task cancelled");
                return headers;
            }

            if (headersTask.IsFaulted)
            {
                _sinceLastTimeout = 0;
                if (headersTask.Exception?.InnerExceptions.Any(x => x.InnerException is TimeoutException) ?? false)
                {
                    _syncBatchSize.Shrink();
                    if (_logger.IsTrace) _logger.Error("Failed to retrieve headers when synchronizing (Timeout)", headersTask.Exception);
                }
                else
                {
                    if (_logger.IsError) _logger.Error("Failed to retrieve headers when synchronizing", headersTask.Exception);
                }

                throw new EthSynchronizationException("Headers task faulted.", headersTask.Exception);
            }

            ValidateSeals(cancellation, headers);
            ValidateBatchConsistency(bestPeer, headers);
            return headers;
        }
        
        private void ValidateBatchConsistency(PeerInfo bestPeer, BlockHeader[] headers)
        {
            // Parity 1.11 non canonical blocks when testing on 27/06
            for (int i = 0; i < headers.Length; i++)
            {
                if (i != 0 && headers[i]?.ParentHash != headers[i - 1]?.Hash)
                {
                    if (_logger.IsTrace) _logger.Trace($"Inconsistent block list from peer {bestPeer}");
                    throw new EthSynchronizationException("Peer sent an inconsistent block list");
                }
            }
        }

        private void ValidateSeals(CancellationToken cancellation, BlockHeader[] headers)
        {
            if (_logger.IsTrace) _logger.Trace("Starting seal validation");
            var exceptions = new ConcurrentQueue<Exception>();
            Parallel.For(0, headers.Length, (i, state) =>
            {
                if (cancellation.IsCancellationRequested)
                {
                    if (_logger.IsTrace) _logger.Trace("Returning fom seal validation");
                    state.Stop();
                    return;
                }

                BlockHeader header = headers[i];
                if (header == null)
                {
                    return;
                }
                
                try
                {
                    if (!_sealValidator.ValidateSeal(headers[i]))
                    {
                        if (_logger.IsTrace) _logger.Trace("One of the seals is invalid");
                        throw new EthSynchronizationException("Peer sent a block with an invalid seal");
                    }
                }
                catch (Exception e)
                {
                    exceptions.Enqueue(e);
                    state.Stop();
                }
            });

            if (_logger.IsTrace) _logger.Trace("Seal validation complete");

            if (exceptions.Count > 0)
            {
                if (_logger.IsDebug) _logger.Debug("Seal validation failure");
                throw new AggregateException(exceptions);
            }
        }

        private bool HandleAddResult(BlockHeader block, bool isFirstInBatch, AddBlockResult addResult)
        {
            switch (addResult)
            {
                case AddBlockResult.UnknownParent:
                {
                    if (_logger.IsTrace) _logger.Trace($"Block/header {block.Number} ignored (unknown parent)");
                    if (isFirstInBatch)
                    {
                        const string message = "Peer sent orphaned blocks/headers inside the batch";
                        _logger.Error(message);
                        throw new EthSynchronizationException(message);
                    }
                    else
                    {
                        const string message = "Peer sent an inconsistent batch of blocks/headers";
                        _logger.Error(message);
                        throw new EthSynchronizationException(message);
                    }
                }
                case AddBlockResult.CannotAccept:
                    throw new EthSynchronizationException("Block tree rejected block/header");
                case AddBlockResult.InvalidBlock:
                    throw new EthSynchronizationException("Peer sent an invalid block/header");
                case AddBlockResult.Added:
                    if (_logger.IsTrace) _logger.Trace($"Block/header {block.Number} suggested for processing");
                    return true;
                case AddBlockResult.AlreadyKnown:
                    if (_logger.IsTrace) _logger.Trace($"Block/header {block.Number} skipped - already known");
                    return false;
                default:
                    throw new NotImplementedException($"Unknown {nameof(AddBlockResult)} {addResult}");
            }
        }
    }
}