#if ALTCOINS
using System.Globalization;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Services.Altcoins.Pirate.Payments
{
    public class PiratePaymentType : PaymentType
    {
        public static PiratePaymentType Instance { get; } = new PiratePaymentType();
        public override string ToPrettyString() => "On-Chain";

        public override string GetId() => "PirateLike";
        public override string ToStringNormalized()
        {
            return "Pirate";
        }

        public override CryptoPaymentData DeserializePaymentData(BTCPayNetworkBase network, string str)
        {
            return JsonConvert.DeserializeObject<PirateLikePaymentData>(str);
        }

        public override string SerializePaymentData(BTCPayNetworkBase network, CryptoPaymentData paymentData)
        {
            return JsonConvert.SerializeObject(paymentData);
        }

        public override IPaymentMethodDetails DeserializePaymentMethodDetails(BTCPayNetworkBase network, string str)
        {
            return JsonConvert.DeserializeObject<PirateLikeOnChainPaymentMethodDetails>(str);
        }

        public override string SerializePaymentMethodDetails(BTCPayNetworkBase network, IPaymentMethodDetails details)
        {
            return JsonConvert.SerializeObject(details);
        }

        public override ISupportedPaymentMethod DeserializeSupportedPaymentMethod(BTCPayNetworkBase network, JToken value)
        {
            return JsonConvert.DeserializeObject<PirateSupportedPaymentMethod>(value.ToString());
        }

        public override string GetTransactionLink(BTCPayNetworkBase network, string txId)
        {
            return string.Format(CultureInfo.InvariantCulture, network.BlockExplorerLink, txId);
        }

        public override string GetPaymentLink(BTCPayNetworkBase network, IPaymentMethodDetails paymentMethodDetails, Money cryptoInfoDue, string serverUri)
        {
            return paymentMethodDetails.Activated
                ? $"{(network as PirateLikeSpecificBtcPayNetwork).UriScheme}:{paymentMethodDetails.GetPaymentDestination()}?amount={cryptoInfoDue.ToDecimal(MoneyUnit.BTC)}"
                : string.Empty;
        }

        public override string InvoiceViewPaymentPartialName { get; } = "Pirate/ViewPirateLikePaymentData";
        public override object GetGreenfieldData(ISupportedPaymentMethod supportedPaymentMethod, bool canModifyStore)
        {
            if (supportedPaymentMethod is PirateSupportedPaymentMethod pirateSupportedPaymentMethod)
            {
                return new
                {
                    pirateSupportedPaymentMethod.AccountIndex,
                };
            }

            return null;
        }

        public override void PopulateCryptoInfo(PaymentMethod details, InvoiceCryptoInfo invoiceCryptoInfo, string serverUrl)
        {
            
        }
    }
}
#endif
