#if ALTCOINS
using System;
using System.Linq;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Configuration;
using BTCPayServer.Payments;
using BTCPayServer.Services.Altcoins.Pirate.Configuration;
using BTCPayServer.Services.Altcoins.Pirate.Payments;
using BTCPayServer.Services.Altcoins.Pirate.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Services.Altcoins.Pirate
{
    public static class PirateLikeExtensions
    {
        public static IServiceCollection AddPirateLike(this IServiceCollection serviceCollection)
        {
            serviceCollection.AddSingleton(provider =>
                provider.ConfigurePirateLikeConfiguration());
            serviceCollection.AddSingleton<PirateRPCProvider>();
            serviceCollection.AddHostedService<PirateLikeSummaryUpdaterHostedService>();
            serviceCollection.AddHostedService<PirateListener>();
            serviceCollection.AddSingleton<PirateAddressPoolService>();
            serviceCollection.AddSingleton<PirateLikePaymentMethodHandler>();
            serviceCollection.AddSingleton<IPaymentMethodHandler>(provider => provider.GetService<PirateLikePaymentMethodHandler>());
            serviceCollection.AddSingleton<IUIExtension>(new UIExtension("Pirate/StoreNavPirateExtension",  "store-nav"));
            serviceCollection.AddSingleton<ISyncSummaryProvider, PirateSyncSummaryProvider>();

            return serviceCollection;
        }

        private static PirateLikeConfiguration ConfigurePirateLikeConfiguration(this IServiceProvider serviceProvider)
        {
            var configuration = serviceProvider.GetService<IConfiguration>();
            var btcPayNetworkProvider = serviceProvider.GetService<BTCPayNetworkProvider>();
            var result = new PirateLikeConfiguration();

            var supportedChains = configuration.GetOrDefault<string>("chains", string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.ToUpperInvariant());

            var supportedNetworks = btcPayNetworkProvider.Filter(supportedChains.ToArray()).GetAll()
                .OfType<PirateLikeSpecificBtcPayNetwork>();

            foreach (var pirateLikeSpecificBtcPayNetwork in supportedNetworks)
            {
                var daemonUri =
                    configuration.GetOrDefault<Uri>($"{pirateLikeSpecificBtcPayNetwork.CryptoCode}_daemon_uri",
                        null);
                var walletDaemonUri =
                    configuration.GetOrDefault<Uri>(
                        $"{pirateLikeSpecificBtcPayNetwork.CryptoCode}_wallet_daemon_uri", null);
                var walletDaemonWalletDirectory =
                    configuration.GetOrDefault<string>(
                        $"{pirateLikeSpecificBtcPayNetwork.CryptoCode}_wallet_daemon_walletdir", null);
                if (daemonUri == null || walletDaemonUri == null)
                {
                    throw new ConfigException($"{pirateLikeSpecificBtcPayNetwork.CryptoCode} is misconfigured");
                }

                result.PirateLikeConfigurationItems.Add(pirateLikeSpecificBtcPayNetwork.CryptoCode, new PirateLikeConfigurationItem()
                {
                    DaemonRpcUri = daemonUri,
                    InternalWalletRpcUri = walletDaemonUri,
                    WalletDirectory = walletDaemonWalletDirectory
                });
            }
            return result;
        }
    }
}
#endif
