﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lykke.Service.Stellar.Api.Core.Domain;
using Lykke.Service.Stellar.Api.Core.Domain.Transaction;
using Lykke.Service.Stellar.Api.Core.Domain.Balance;

namespace Lykke.Service.Stellar.Api.Core.Services
{
    public interface IStellarService
    {
        Boolean IsAddressValid(string address);

        Task<TxBroadcast> GetTxBroadcastAsync(Guid operationId);

        Task BroadcastTxAsync(Guid operationId, string xdrBase64);

        Task DeleteTxBroadcastAsync(Guid operationId);

        Task<Fees> GetFeesAsync();

        Task<TxBuild> GetTxBuildAsync(Guid operationId);

        Task<string> BuildTransactionAsync(Guid operationId, AddressBalance from, string toAddress, long amount);
    }
}
