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
                    "ARRR_X = ARRR_BTC * BTC_X",
                    "ARRR_BTC = nonkyc_io(ARRR_BTC)"
                },
                CryptoImagePath = "/imlegacy/pirate.png",
                UriScheme = "Pirate"
            });
        }
    }
}
