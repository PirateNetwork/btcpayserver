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

        /// <summary>"sapling" or "ironwood" - the pool this account was fixed to at creation time.</summary>
        [JsonProperty("pool")] public string Pool { get; set; }
    }
}
