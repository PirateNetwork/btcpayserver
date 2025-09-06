#if ALTCOINS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Logging;
using BTCPayServer.Models;
using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Payments;
using BTCPayServer.Rating;
using BTCPayServer.Services.Altcoins.Pirate.RPC.Models;
using BTCPayServer.Services.Altcoins.Pirate.Services;
using BTCPayServer.Services.Altcoins.Pirate.Utils;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Rates;
using NBitcoin;

namespace BTCPayServer.Services.Altcoins.Pirate.Payments
{
    public class PirateLikePaymentMethodHandler : PaymentMethodHandlerBase<PirateSupportedPaymentMethod, PirateLikeSpecificBtcPayNetwork>
    {
        private readonly BTCPayNetworkProvider _networkProvider;
        private readonly PirateRPCProvider _pirateRpcProvider;

        public PirateLikePaymentMethodHandler(BTCPayNetworkProvider networkProvider, PirateRPCProvider pirateRpcProvider)
        {
            _networkProvider = networkProvider;
            _pirateRpcProvider = pirateRpcProvider;
        }
        public override PaymentType PaymentType => PiratePaymentType.Instance;

        public override async Task<IPaymentMethodDetails> CreatePaymentMethodDetails(InvoiceLogs logs, PirateSupportedPaymentMethod supportedPaymentMethod, PaymentMethod paymentMethod,
            StoreData store, PirateLikeSpecificBtcPayNetwork network, object preparePaymentObject)
        {
            
            if (preparePaymentObject is null)
            {
                return new PirateLikeOnChainPaymentMethodDetails()
                {
                    Activated = false
                };
            }

            if (!_pirateRpcProvider.IsAvailable(network.CryptoCode))
                throw new PaymentMethodUnavailableException($"Node or wallet not available");
            var invoice = paymentMethod.ParentEntity;
            if (!(preparePaymentObject is Prepare piratePrepare))
                throw new ArgumentException();
            var address = await piratePrepare.ReserveAddress(invoice.Id);

            return new PirateLikeOnChainPaymentMethodDetails()
            {
                NextNetworkFee = PirateMoney.Convert(10000),
                AccountIndex = supportedPaymentMethod.AccountIndex,
                AddressIndex = address.AddressIndex,
                DepositAddress = address.Address,
                Activated = true
            };

        }

        public override object PreparePayment(PirateSupportedPaymentMethod supportedPaymentMethod, StoreData store,
            BTCPayNetworkBase network)
        {

            var walletClient = _pirateRpcProvider.WalletRpcClients[supportedPaymentMethod.CryptoCode];
            var daemonClient = _pirateRpcProvider.DaemonRpcClients[supportedPaymentMethod.CryptoCode];
            return new Prepare()
            {
                ReserveAddress = s => walletClient.SendCommandAsync<CreateAddressRequest, CreateAddressResponse>("create_address", new CreateAddressRequest() { Label = $"btcpay invoice #{s}", AccountIndex = supportedPaymentMethod.AccountIndex })
            };
        }

        class Prepare
        {
            public Func<string, Task<CreateAddressResponse>> ReserveAddress;
        }

        public override void PreparePaymentModel(PaymentModel model, InvoiceResponse invoiceResponse,
            StoreBlob storeBlob, IPaymentMethod paymentMethod)
        {
            var paymentMethodId = paymentMethod.GetId();
            var network = _networkProvider.GetNetwork<PirateLikeSpecificBtcPayNetwork>(model.CryptoCode);
            model.PaymentMethodName = GetPaymentMethodName(network);
            model.CryptoImage = GetCryptoImage(network);
            if (model.Activated)
            {
                var cryptoInfo = invoiceResponse.CryptoInfo.First(o => o.GetpaymentMethodId() == paymentMethodId);
                model.InvoiceBitcoinUrl = PiratePaymentType.Instance.GetPaymentLink(network,
                    new PirateLikeOnChainPaymentMethodDetails() {DepositAddress = cryptoInfo.Address}, cryptoInfo.Due,
                    null);
                model.InvoiceBitcoinUrlQR = model.InvoiceBitcoinUrl;
            }
            else
            {
                model.InvoiceBitcoinUrl = "";
                model.InvoiceBitcoinUrlQR = "";
            }
        }
        public override string GetCryptoImage(PaymentMethodId paymentMethodId)
        {
            var network = _networkProvider.GetNetwork<PirateLikeSpecificBtcPayNetwork>(paymentMethodId.CryptoCode);
            return GetCryptoImage(network);
        }

        public override string GetPaymentMethodName(PaymentMethodId paymentMethodId)
        {
            var network = _networkProvider.GetNetwork<PirateLikeSpecificBtcPayNetwork>(paymentMethodId.CryptoCode);
            return GetPaymentMethodName(network);
        }
        public override IEnumerable<PaymentMethodId> GetSupportedPaymentMethods()
        {
            return _networkProvider.GetAll()
                .Where(network => network is PirateLikeSpecificBtcPayNetwork)
                .Select(network => new PaymentMethodId(network.CryptoCode, PaymentType));
        }

        private string GetCryptoImage(PirateLikeSpecificBtcPayNetwork network)
        {
            return network.CryptoImagePath;
        }


        private string GetPaymentMethodName(PirateLikeSpecificBtcPayNetwork network)
        {
            return $"{network.DisplayName}";
        }
    }
}
#endif
