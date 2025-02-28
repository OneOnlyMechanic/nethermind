// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Eth;

public class BlockForRpc
{
    private readonly BlockDecoder _blockDecoder = new();
    private readonly bool _isAuRaBlock;

    protected BlockForRpc()
    {

    }

    public BlockForRpc(Block block, bool includeFullTransactionData, ISpecProvider specProvider)
    {
        _isAuRaBlock = block.Header.AuRaSignature is not null;
        Author = block.Author ?? block.Beneficiary;
        Difficulty = block.Difficulty;
        ExtraData = block.ExtraData;
        GasLimit = block.GasLimit;
        GasUsed = block.GasUsed;
        Hash = block.Hash;
        LogsBloom = block.Bloom;
        Miner = block.Beneficiary;
        if (!_isAuRaBlock)
        {
            MixHash = block.MixHash;
            Nonce = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(Nonce, block.Nonce);
        }
        else
        {
            Step = block.Header.AuRaStep;
            Signature = block.Header.AuRaSignature;
        }

        if (specProvider is not null)
        {
            var spec = specProvider.GetSpec(block.Header);
            if (spec.IsEip1559Enabled)
            {
                BaseFeePerGas = block.Header.BaseFeePerGas;
            }

            if (spec.IsEip4844Enabled)
            {
                DataGasUsed = block.Header.DataGasUsed;
                ExcessDataGas = block.Header.ExcessDataGas;
            }
        }

        Number = block.Number;
        ParentHash = block.ParentHash;
        ReceiptsRoot = block.ReceiptsRoot;
        Sha3Uncles = block.UnclesHash;
        Size = _blockDecoder.GetLength(block, RlpBehaviors.None);
        StateRoot = block.StateRoot;
        Timestamp = block.Timestamp;
        TotalDifficulty = block.TotalDifficulty ?? 0;
        Transactions = includeFullTransactionData ? block.Transactions.Select((t, idx) => new TransactionForRpc(block.Hash, block.Number, idx, t, block.BaseFeePerGas)).ToArray() : block.Transactions.Select(t => t.Hash).OfType<object>().ToArray();
        TransactionsRoot = block.TxRoot;
        Uncles = block.Uncles.Select(o => o.Hash);
        Withdrawals = block.Withdrawals;
        WithdrawalsRoot = block.Header.WithdrawalsRoot;
    }

    public Address Author { get; set; }
    public UInt256 Difficulty { get; set; }
    public byte[] ExtraData { get; set; }
    public long GasLimit { get; set; }
    public long GasUsed { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Include)]
    public Keccak Hash { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Include)]
    public Bloom LogsBloom { get; set; }
    public Address Miner { get; set; }
    public Keccak MixHash { get; set; }

    public bool ShouldSerializeMixHash() => !_isAuRaBlock && MixHash is not null;

    [JsonProperty(NullValueHandling = NullValueHandling.Include)]
    public byte[] Nonce { get; set; }

    public bool ShouldSerializeNonce() => !_isAuRaBlock;

    [JsonProperty(NullValueHandling = NullValueHandling.Include)]
    public long? Number { get; set; }
    public Keccak ParentHash { get; set; }
    public Keccak ReceiptsRoot { get; set; }
    public Keccak Sha3Uncles { get; set; }
    public byte[] Signature { get; set; }
    public bool ShouldSerializeSignature() => _isAuRaBlock;
    public long Size { get; set; }
    public Keccak StateRoot { get; set; }
    [JsonConverter(typeof(NullableLongConverter), NumberConversion.Raw)]
    public long? Step { get; set; }
    public bool ShouldSerializeStep() => _isAuRaBlock;
    public UInt256 TotalDifficulty { get; set; }
    public UInt256 Timestamp { get; set; }

    public UInt256? BaseFeePerGas { get; set; }
    public IEnumerable<object> Transactions { get; set; }
    public Keccak TransactionsRoot { get; set; }
    public IEnumerable<Keccak> Uncles { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public IEnumerable<Withdrawal>? Withdrawals { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public Keccak? WithdrawalsRoot { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ulong? DataGasUsed { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ulong? ExcessDataGas { get; set; }
}
