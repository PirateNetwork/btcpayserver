using Newtonsoft.Json;

namespace BTCPayServer.Services.Altcoins.Pirate.RPC.Models
{
    public partial class CreateAccountRequest
    {
        [JsonProperty("label")] public string Label { get; set; }

        /// <summary>
        /// "sapling" or "ironwood". Fixes which shielded pool every address
        /// in this account will be generated from. Omit to fall back to the
        /// walletd's own default pool.
        /// </summary>
        [JsonProperty("pool", NullValueHandling = NullValueHandling.Ignore)]
        public string Pool { get; set; }
    }
}
