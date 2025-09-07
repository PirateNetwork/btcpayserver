#if ALTCOINS
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Services.Altcoins.Pirate.RPC.Models;

namespace BTCPayServer.Services.Altcoins.Pirate.Services
{
    /// <summary>
    /// In-memory address pool per crypto/account to work with IVK-based setups.
    /// Ensures we always have a small pool of pre-created subaddresses ready for invoices without requiring wallet restarts.
    /// </summary>
    public class PirateAddressPoolService
    {
        private class AddressPool
        {
            public long AccountIndex { get; set; }
            public ConcurrentQueue<(string Address, long AddressIndex)> Queue { get; } = new ConcurrentQueue<(string, long)>();
            public int InFlightCreations;
            public int Prefilled;
        }

        private readonly PirateRPCProvider _rpcProvider;
        private readonly ConcurrentDictionary<(string CryptoCode, long AccountIndex), AddressPool> _pools = new ConcurrentDictionary<(string, long), AddressPool>();

        private const int MinPoolSize = 20;   // if below this, trigger refill
        private const int RefillBatch = 10;   // when below threshold, add 10 more

        public PirateAddressPoolService(PirateRPCProvider rpcProvider)
        {
            _rpcProvider = rpcProvider;
        }

        private AddressPool GetOrCreatePool(string cryptoCode, long accountIndex)
        {
            return _pools.GetOrAdd((cryptoCode.ToUpperInvariant(), accountIndex), key => new AddressPool
            {
                AccountIndex = accountIndex
            });
        }

        /// <summary>
        /// Reserve the next address from the pool; if the pool is empty, synchronously creates one.
        /// Triggers an async top-up when the pool falls below the threshold.
        /// </summary>
        public async Task<(string Address, long AddressIndex)> ReserveAddress(string cryptoCode, long accountIndex, string invoiceId, CancellationToken cancellationToken = default)
        {
            var pool = GetOrCreatePool(cryptoCode, accountIndex);

            // First use prefill: ensure at least MinPoolSize addresses are available
            if (Volatile.Read(ref pool.Prefilled) == 0)
            {
                await PrefillAsync(cryptoCode, pool, cancellationToken);
                Interlocked.Exchange(ref pool.Prefilled, 1);
            }

            if (!pool.Queue.TryDequeue(out var reserved))
            {
                var client = _rpcProvider.WalletRpcClients[cryptoCode];
                var created = await client.SendCommandAsync<CreateAddressRequest, CreateAddressResponse>(
                    "create_address",
                    new CreateAddressRequest
                    {
                        AccountIndex = accountIndex,
                        Label = $"btcpay invoice #{invoiceId}"
                    }, cancellationToken);
                reserved = (created.Address, created.AddressIndex);
            }

            // Fire-and-forget top-up if needed
            _ = TopUpAsync(cryptoCode, pool);
            return reserved;
        }

        private async Task PrefillAsync(string cryptoCode, AddressPool pool, CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref pool.InFlightCreations, 1, 0) != 0) return;
            try
            {
                var client = _rpcProvider.WalletRpcClients[cryptoCode];
                var toCreate = Math.Max(0, MinPoolSize - pool.Queue.Count);
                for (int i = 0; i < toCreate; i++)
                {
                    var created = await client.SendCommandAsync<CreateAddressRequest, CreateAddressResponse>(
                        "create_address",
                        new CreateAddressRequest
                        {
                            AccountIndex = pool.AccountIndex,
                            Label = "btcpay pool"
                        }, cancellationToken);
                    pool.Queue.Enqueue((created.Address, created.AddressIndex));
                }
            }
            catch
            {
                // ignore; reserve path will still create one synchronously
            }
            finally
            {
                Interlocked.Exchange(ref pool.InFlightCreations, 0);
            }
        }

        private async Task TopUpAsync(string cryptoCode, AddressPool pool)
        {
            // Avoid excessive concurrent creations
            if (pool.Queue.Count >= MinPoolSize) return;
            if (Interlocked.CompareExchange(ref pool.InFlightCreations, 1, 0) != 0) return;
            try
            {
                var client = _rpcProvider.WalletRpcClients[cryptoCode];
                // When below threshold, always add a fixed batch of 10
                var toCreate = RefillBatch;
                for (int i = 0; i < toCreate; i++)
                {
                    var created = await client.SendCommandAsync<CreateAddressRequest, CreateAddressResponse>(
                        "create_address",
                        new CreateAddressRequest
                        {
                            AccountIndex = pool.AccountIndex,
                            Label = "btcpay pool"
                        });
                    pool.Queue.Enqueue((created.Address, created.AddressIndex));
                }
            }
            catch
            {
                // ignore, will retry on next reserve
            }
            finally
            {
                Interlocked.Exchange(ref pool.InFlightCreations, 0);
            }
        }
    }
}
#endif


