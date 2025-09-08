using NBitcoin;

namespace BTCPayServer
{
    public partial class BTCPayNetworkProvider
    {
        public void InitPirate()
        {
            Add(new PirateLikeSpecificBtcPayNetwork()
            {
                CryptoCode = "ARRR",
                DisplayName = "Pirate",
                Divisibility = 8,
                BlockExplorerLink =
                    NetworkType == ChainName.Mainnet
                        ? "https://explorer.piratechain.com/tx/{0}"
                        : "https://explorer.piratechain.com/tx/{0}", 
                DefaultRateRules = new[]
                {
                    "XMR_X = XMR_BTC * BTC_X",
                    "XMR_BTC = kraken(XMR_BTC)"
                },
                CryptoImagePath = "/imlegacy/pirate.png",
                UriScheme = "Pirate"
            });
        }
    }
}
