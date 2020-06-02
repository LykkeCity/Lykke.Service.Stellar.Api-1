﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Chaos.NaCl;
using JetBrains.Annotations;
using Lykke.Service.Stellar.Api.Core;
using Lykke.Service.Stellar.Api.Core.Exceptions;
using Lykke.Service.Stellar.Api.Core.Services;
using stellar_dotnet_sdk;
using stellar_dotnet_sdk.federation;
using stellar_dotnet_sdk.requests;
using stellar_dotnet_sdk.responses;
using stellar_dotnet_sdk.responses.operations;
using stellar_dotnet_sdk.xdr;
using Operation = stellar_dotnet_sdk.xdr.Operation;
using TransactionResult = stellar_dotnet_sdk.xdr.TransactionResult;

namespace Lykke.Service.Stellar.Api.Services.Horizon
{
    public class HorizonService : IHorizonService
    {
        private readonly Uri _horizonUrl;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly Server _server;

        [UsedImplicitly]
        public HorizonService(string network,
                              Uri horizonUrl,
                              IHttpClientFactory httpClientFactory,
                              Server server)
        {
            if (network == "")
                Network.UsePublicNetwork();
            else
                Network.UseTestNetwork();

            _horizonUrl = horizonUrl;
            _httpClientFactory = httpClientFactory;
            _server = server;
        }

        public async Task<string> SubmitTransactionAsync(string signedTx)
        {
            // submit a tx
            var transaction = stellar_dotnet_sdk.Transaction.FromEnvelopeXdr(signedTx);

            var tx = await _server.SubmitTransaction(transaction);

            if (tx == null || string.IsNullOrEmpty(tx.Hash))
            {
                throw new HorizonApiException("Submitting transaction failed. No valid transaction was returned.");
            }

            return tx.Hash;
        }

        public async Task<TransactionResponse> GetTransactionDetails(string hash)
        {
            try
            {
                var builder = new TransactionsRequestBuilder(_horizonUrl, _httpClientFactory.CreateClient());
                var tx = await builder.Transaction(hash);

                return tx;
            }
            catch (NotFoundException)
            {
                // transaction not found
                return null;
            }
        }

        public async Task<List<TransactionResponse>> GetTransactions(string address,
            OrderDirection order = OrderDirection.ASC, string cursor = "", int limit = 100)
        {
            try
            {
                var builder = new TransactionsRequestBuilder(_horizonUrl, _httpClientFactory.CreateClient());
                builder.ForAccount(address);
                builder.Order(order).Cursor(cursor).Limit(limit);
                var details = await builder.Execute();
                var transactions = details?.Embedded?.Records;
                if (transactions != null)
                {
                    return transactions
                        .Where(tx => GetTransactionResult(tx) == TransactionResultCode.TransactionResultCodeEnum.txSUCCESS)
                        .ToList();
                }
            }
            catch (NotFoundException)
            {
                // address not found
            }

            return new List<TransactionResponse>();
        }

        public async Task<List<OperationResponse>> GetTransactionOperations(string hash)
        {
            var result = await new OperationsRequestBuilder(_horizonUrl, _httpClientFactory.CreateClient())
                .ForTransaction(hash)
                .Execute();

            return result?.Records;
        }

        public async Task<LedgerResponse> GetLatestLedger()
        {
            var builder = new LedgersRequestBuilder(_horizonUrl, _httpClientFactory.CreateClient());
            builder.Order(OrderDirection.DESC).Limit(1);
            var ledgers = await builder.Execute();
            if (ledgers?.Embedded?.Records == null || ledgers.Embedded?.Records.Count < 1)
            {
                throw new HorizonApiException("Latest ledger missing from query result.");
            }
            return ledgers.Embedded.Records[0];
        }

        public async Task<AccountResponse> GetAccountDetails(string address)
        {
            try
            {
                var builder = new AccountsRequestBuilder(_horizonUrl, _httpClientFactory.CreateClient());
                var accountDetails = await builder.Account(address);
                
                return accountDetails;
            }
            catch (NotFoundException)
            {
                // address not found
                return null;
            }
        }

        public async Task<bool> AccountExists(string address)
        {
            var accountDetails = await GetAccountDetails(address);
            return accountDetails != null;
        }

        public long GetAccountMergeAmount(string resultXdrBase64, int operationIndex)
        {
            var xdr = Convert.FromBase64String(resultXdrBase64);
            var txResult = TransactionMeta.Decode(new XdrDataInputStream(xdr));
            var merge = txResult.Operations[operationIndex];
            var result = merge?.Changes?.InnerValue;
            var resultCode = result?.Discriminant?.InnerValue;
            if (resultCode == null ||
                resultCode != AccountMergeResultCode.AccountMergeResultCodeEnum.ACCOUNT_MERGE_SUCCESS) return 0;

            var amount = result.SourceAccountBalance.InnerValue;
            return amount;
        }

        public long GetAccountMergeAmount(string metaXdrBase64, string sourceAddress)
        {
            var xdr = Convert.FromBase64String(metaXdrBase64);
            var reader = new ByteReader(xdr);
            var txMeta = StellarBase.Generated.TransactionMeta.Decode(reader);
            var mergeMeta = txMeta.Operations.First(op =>
            {
                return op.Changes.InnerValue.Any(c =>
                {
                    return c.Discriminant.InnerValue == LedgerEntryChangeType.LedgerEntryChangeTypeEnum.LEDGER_ENTRY_REMOVED &&
                        KeyPair.FromXdrPublicKey(c.Removed.Account.AccountID.InnerValue).Address == sourceAddress;
                });
            });
            var sourceAccountStateMeta = mergeMeta.Changes.InnerValue.First(c =>
                c.Discriminant.InnerValue == LedgerEntryChangeType.LedgerEntryChangeTypeEnum.LEDGER_ENTRY_STATE && KeyPair.FromXdrPublicKey(c.State.Data.Account.AccountID.InnerValue).Address == sourceAddress);

            return sourceAccountStateMeta.State.Data.Account.Balance.InnerValue;
        }

        public Operation.OperationBody GetFirstOperationFromTxEnvelopeXdr(string xdrBase64)
        {
            var xdr = Convert.FromBase64String(xdrBase64);
            var reader = new ByteReader(xdr);
            var txEnvelope = TransactionEnvelope.Decode(reader);
            return GetFirstOperationFromTxEnvelope(txEnvelope);
        }

        public Operation.OperationBody GetFirstOperationFromTxEnvelope(TransactionEnvelope txEnvelope)
        {
            if (txEnvelope?.Tx?.Operations == null || txEnvelope.Tx.Operations.Length < 1 ||
                txEnvelope.Tx.Operations[0].Body == null)
            {
                throw new HorizonApiException($"Failed to extract first operation from transaction.");
            }

            var operation = txEnvelope.Tx.Operations[0].Body;
            return operation;
        }

        public string GetMemo(TransactionResponse tx)
        {
            if ((StellarSdkConstants.MemoTextTypeName.Equals(tx.MemoType, StringComparison.OrdinalIgnoreCase) ||
                StellarSdkConstants.MemoIdTypeName.Equals(tx.MemoType, StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrEmpty(tx.Memo))
            {
                return tx.Memo;
            }

            return null;
        }

        public string GetTransactionHash(StellarBase.Generated.Transaction tx)
        {
            var writer = new ByteWriter();

            // Hashed NetworkID
            writer.Write(Network.CurrentNetworkId);

            // Envelope Type - 4 bytes
            EnvelopeType.Encode(writer, EnvelopeType.Create(EnvelopeType.EnvelopeTypeEnum.ENVELOPE_TYPE_TX));

            // Transaction XDR bytes
            var txWriter = new ByteWriter();
            StellarBase.Generated.Transaction.Encode(txWriter, tx);
            writer.Write(txWriter.ToArray());

            var data = writer.ToArray();
            var hash = Utilities.Hash(data);
            return CryptoBytes.ToHexStringLower(hash);
        }

        public TransactionResultCode.TransactionResultCodeEnum GetTransactionResult(TransactionResponse tx)
        {
            var xdr = Convert.FromBase64String(tx.ResultXdr);
            var reader = new ByteReader(xdr);
            var txResult = StellarBase.Generated.TransactionResult.Decode(reader);
            
            return txResult.Result.Discriminant.InnerValue;
        }
    }
}
