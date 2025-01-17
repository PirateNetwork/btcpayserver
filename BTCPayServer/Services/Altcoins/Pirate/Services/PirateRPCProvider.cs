#if ALTCOINS
using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net.Http;
using System.Threading.Tasks;
using BTCPayServer.Services.Altcoins.Pirate.Configuration;
using BTCPayServer.Services.Altcoins.Pirate.RPC;
using BTCPayServer.Services.Altcoins.Pirate.RPC.Models;
using NBitcoin;

namespace BTCPayServer.Services.Altcoins.Pirate.Services
{
    public class PirateRPCProvider
    {
        private readonly PirateLikeConfiguration _pirateLikeConfiguration;
        private readonly EventAggregator _eventAggregator;
        public ImmutableDictionary<string, JsonRpcClient> DaemonRpcClients;
        public ImmutableDictionary<string, JsonRpcClient> WalletRpcClients;

        private readonly ConcurrentDictionary<string, PirateLikeSummary> _summaries =
            new ConcurrentDictionary<string, PirateLikeSummary>();

        public ConcurrentDictionary<string, PirateLikeSummary> Summaries => _summaries;

        public PirateRPCProvider(PirateLikeConfiguration pirateLikeConfiguration, EventAggregator eventAggregator, IHttpClientFactory httpClientFactory)
        {
            _pirateLikeConfiguration = pirateLikeConfiguration;
            _eventAggregator = eventAggregator;
            DaemonRpcClients =
                _pirateLikeConfiguration.PirateLikeConfigurationItems.ToImmutableDictionary(pair => pair.Key,
                    pair => new JsonRpcClient(pair.Value.DaemonRpcUri, "", "", httpClientFactory.CreateClient()));
            WalletRpcClients =
                _pirateLikeConfiguration.PirateLikeConfigurationItems.ToImmutableDictionary(pair => pair.Key,
                    pair => new JsonRpcClient(pair.Value.InternalWalletRpcUri, "", "", httpClientFactory.CreateClient()));
        }

        public bool IsAvailable(string cryptoCode)
        {
            cryptoCode = cryptoCode.ToUpperInvariant();
            return _summaries.ContainsKey(cryptoCode) && IsAvailable(_summaries[cryptoCode]);
        }

        private bool IsAvailable(PirateLikeSummary summary)
        {
            return summary.Synced &&
                   summary.WalletAvailable;
        }

        public async Task<PirateLikeSummary> UpdateSummary(string cryptoCode)
        {
            if (!DaemonRpcClients.TryGetValue(cryptoCode.ToUpperInvariant(), out var daemonRpcClient) ||
                !WalletRpcClients.TryGetValue(cryptoCode.ToUpperInvariant(), out var walletRpcClient))
            {
                return null;
            }

            var summary = new PirateLikeSummary();
            try
            {
                var daemonResult =
                    await daemonRpcClient.SendCommandAsync<JsonRpcClient.NoRequestModel, SyncInfoResponse>("sync_info",
                        JsonRpcClient.NoRequestModel.Instance);
                summary.CurrentHeight = daemonResult.Height;
                summary.Synced = true; // The Pirate lightwalletd server does not show its synced status
                summary.UpdatedAt = DateTime.UtcNow;
                summary.DaemonAvailable = true;
            }
            catch
            {
                summary.DaemonAvailable = false;
            }

            try
            {
                var walletResult =
                    await walletRpcClient.SendCommandAsync<JsonRpcClient.NoRequestModel, GetHeightResponse>(
                        "get_height", JsonRpcClient.NoRequestModel.Instance);

                summary.WalletHeight = walletResult.Height;
                summary.WalletAvailable = true;
            }
            catch
            {
                summary.WalletAvailable = false;
            }

            var changed = !_summaries.ContainsKey(cryptoCode) || IsAvailable(cryptoCode) != IsAvailable(summary);

            _summaries.AddOrReplace(cryptoCode, summary);
            if (changed)
            {
                _eventAggregator.Publish(new PirateDaemonStateChange() { Summary = summary, CryptoCode = cryptoCode });
            }

            return summary;
        }


        public class PirateDaemonStateChange
        {
            public string CryptoCode { get; set; }
            public PirateLikeSummary Summary { get; set; }
        }

        public class PirateLikeSummary
        {
            public bool Synced { get; set; }
            public long CurrentHeight { get; set; }
            public long WalletHeight { get; set; }
            public DateTime UpdatedAt { get; set; }
            public bool DaemonAvailable { get; set; }
            public bool WalletAvailable { get; set; }
        }
    }
}
#endif
