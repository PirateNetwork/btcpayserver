using Newtonsoft.Json;

namespace BTCPayServer.Services.Altcoins.Pirate.RPC.Models
{
    public partial class CreateAddressRequest
    {
        [JsonProperty("account_index")] public long AccountIndex { get; set; }
        [JsonProperty("label")] public string Label { get; set; }
    }
}
