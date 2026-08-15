using Newtonsoft.Json;

namespace BTCPayServer.Services.Altcoins.Pirate.RPC.Models
{
    public partial class SubaddressAccount
    {
        [JsonProperty("account_index")] public long AccountIndex { get; set; }
        [JsonProperty("balance")] public decimal Balance { get; set; }
        [JsonProperty("base_address")] public string BaseAddress { get; set; }
        [JsonProperty("label")] public string Label { get; set; }
        [JsonProperty("tag")] public string Tag { get; set; }
        [JsonProperty("unlocked_balance")] public decimal UnlockedBalance { get; set; }

        /// <summary>"sapling" or "ironwood" - the pool addresses in this account currently come from.</summary>
        [JsonProperty("pool")] public string Pool { get; set; }

        /// <summary>
        /// True if <see cref="Pool"/> was explicitly pinned and will never change; false if it's
        /// just today's resolution of the wallet's default and may switch later (e.g. once Ironwood
        /// activates on the network).
        /// </summary>
        [JsonProperty("pool_pinned")] public bool PoolPinned { get; set; }
    }
}
