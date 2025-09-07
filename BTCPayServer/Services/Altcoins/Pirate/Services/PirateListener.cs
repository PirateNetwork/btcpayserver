#if ALTCOINS
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Services.Altcoins.Pirate.Configuration;
using BTCPayServer.Services.Altcoins.Pirate.Payments;
using BTCPayServer.Services.Altcoins.Pirate.RPC;
using BTCPayServer.Services.Altcoins.Pirate.RPC.Models;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer;

namespace BTCPayServer.Services.Altcoins.Pirate.Services
{
    public class PirateListener : IHostedService
    {
        private readonly InvoiceRepository _invoiceRepository;
        private readonly EventAggregator _eventAggregator;
        private readonly PirateRPCProvider _pirateRpcProvider;
        private readonly PirateLikeConfiguration _PirateLikeConfiguration;
        private readonly BTCPayNetworkProvider _networkProvider;
        private readonly ILogger<PirateListener> _logger;
        private readonly PaymentService _paymentService;
        private readonly CompositeDisposable leases = new CompositeDisposable();
        private readonly Queue<Func<CancellationToken, Task>> taskQueue = new Queue<Func<CancellationToken, Task>>();
        private CancellationTokenSource _Cts;

        public PirateListener(InvoiceRepository invoiceRepository,
            EventAggregator eventAggregator,
            PirateRPCProvider pirateRpcProvider,
            PirateLikeConfiguration pirateLikeConfiguration,
            BTCPayNetworkProvider networkProvider,
            ILogger<PirateListener> logger, 
            PaymentService paymentService)
        {
            _invoiceRepository = invoiceRepository;
            _eventAggregator = eventAggregator;
            _pirateRpcProvider = pirateRpcProvider;
            _PirateLikeConfiguration = pirateLikeConfiguration;
            _networkProvider = networkProvider;
            _logger = logger;
            _paymentService = paymentService;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (!_PirateLikeConfiguration.PirateLikeConfigurationItems.Any())
            {
                return Task.CompletedTask;
            }
            _Cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            leases.Add(_eventAggregator.Subscribe<PirateEvent>(OnPirateEvent));
            leases.Add(_eventAggregator.Subscribe<PirateRPCProvider.PirateDaemonStateChange>(e =>
            {
                if (_pirateRpcProvider.IsAvailable(e.CryptoCode))
                {
                    _logger.LogInformation($"{e.CryptoCode} just became available");
                    _ = UpdateAnyPendingPirateLikePayment(e.CryptoCode);
                }
                else
                {
                    _logger.LogInformation($"{e.CryptoCode} just became unavailable");
                }
            }));
            // Periodic poll in case callbacks are missed
            leases.Add(_eventAggregator.Subscribe<NewBlockEvent>(e => { }));
            _ = Task.Run(async () =>
            {
                while (!_Cts.IsCancellationRequested)
                {
                    try
                    {
                        // Continuously ask walletd to scan, then update pending invoices
                        foreach (var item in _PirateLikeConfiguration.PirateLikeConfigurationItems.Keys)
                        {
                            try
                            {
                                await _pirateRpcProvider.WalletRpcClients[item]
                                    .SendCommandAsync<Empty, Empty>("request_scan", new Empty());
                            }
                            catch (Exception ex) when (!_Cts.IsCancellationRequested)
                            {
                                _logger.LogWarning(ex, "Pirate periodic request_scan failed");
                            }
                        }
                        foreach (var item in _PirateLikeConfiguration.PirateLikeConfigurationItems.Keys)
                        {
                            await UpdateAnyPendingPirateLikePayment(item);
                        }
                    }
                    catch (Exception ex) when (!_Cts.IsCancellationRequested)
                    {
                        _logger.LogError(ex, "Periodic Pirate poll failed");
                    }
                    await Task.Delay(TimeSpan.FromSeconds(10), _Cts.Token);
                }
            }, _Cts.Token);
            // React immediately to newly created invoices by kicking an on-demand scan
            leases.Add(_eventAggregator.Subscribe<InvoiceEvent>(inv =>
            {
                if (inv.Name != InvoiceEvent.Created)
                    return;

                foreach (var cryptoCode in _PirateLikeConfiguration.PirateLikeConfigurationItems.Keys)
                {
                    var invoiceId = inv.Invoice.Id;
                    taskQueue.Enqueue(token => UpdatePaymentsForInvoice(cryptoCode, invoiceId));
                    // Also trigger a walletd scan immediately
                    taskQueue.Enqueue(async token =>
                    {
                        try
                        {
                            await _pirateRpcProvider.WalletRpcClients[cryptoCode]
                                .SendCommandAsync<Empty, Empty>("request_scan", new Empty());
                        }
                        catch (Exception ex) when (!_Cts.IsCancellationRequested)
                        {
                            _logger.LogWarning(ex, "Pirate request_scan on invoice_created failed");
                        }
                    });
                }
            }));
            _ = WorkThroughQueue(_Cts.Token);
            return Task.CompletedTask;
        }

        private async Task WorkThroughQueue(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (taskQueue.TryDequeue(out var t))
                {
                    try
                    {

                        await t.Invoke(token);
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "error with queue item");
                    }
                }
                else
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                }
            }
        }

        private void OnPirateEvent(PirateEvent obj)
        {
            if (!_pirateRpcProvider.IsAvailable(obj.CryptoCode))
            {
                return;
            }

            if (!string.IsNullOrEmpty(obj.BlockHash))
            {
                taskQueue.Enqueue(token => OnNewBlock(obj.CryptoCode));
            }

            if (!string.IsNullOrEmpty(obj.TransactionHash))
            {
                taskQueue.Enqueue(token => OnTransactionUpdated(obj.CryptoCode, obj.TransactionHash));
            }
        }

        private async Task ReceivedPayment(InvoiceEntity invoice, PaymentEntity payment)
        {
            _logger.LogInformation(
                $"Invoice {invoice.Id} received payment {payment.GetCryptoPaymentData().GetValue()} {payment.GetCryptoCode()} {payment.GetCryptoPaymentData().GetPaymentId()}");
            var paymentData = (PirateLikePaymentData)payment.GetCryptoPaymentData();
            var paymentMethod = invoice.GetPaymentMethod(payment.Network, PiratePaymentType.Instance);
            if (paymentMethod != null &&
                paymentMethod.GetPaymentMethodDetails() is PirateLikeOnChainPaymentMethodDetails pirate &&
                pirate.Activated && 
                pirate.GetPaymentDestination() == paymentData.GetDestination() &&
                paymentMethod.Calculate().Due > Money.Zero)
            {
                var walletClient = _pirateRpcProvider.WalletRpcClients[payment.GetCryptoCode()];

                var address = await walletClient.SendCommandAsync<CreateAddressRequest, CreateAddressResponse>(
                    "create_address",
                    new CreateAddressRequest()
                    {
                        Label = $"btcpay invoice #{invoice.Id}",
                        AccountIndex = pirate.AccountIndex
                    });
                pirate.DepositAddress = address.Address;
                pirate.AddressIndex = address.AddressIndex;
                await _invoiceRepository.NewPaymentDetails(invoice.Id, pirate, payment.Network);
                _eventAggregator.Publish(
                    new InvoiceNewPaymentDetailsEvent(invoice.Id, pirate, payment.GetPaymentMethodId()));
                paymentMethod.SetPaymentMethodDetails(pirate);
                invoice.SetPaymentMethod(paymentMethod);
            }

            _eventAggregator.Publish(
                new InvoiceEvent(invoice, InvoiceEvent.ReceivedPayment) { Payment = payment });
        }

        private async Task UpdatePaymentStates(string cryptoCode, InvoiceEntity[] invoices)
        {
            if (!invoices.Any())
            {
                return;
            }

            var pirateWalletRpcClient = _pirateRpcProvider.WalletRpcClients[cryptoCode];
            var network = _networkProvider.GetNetwork(cryptoCode);


            //get all the required data in one list (invoice, its existing payments and the current payment method details)
            var expandedInvoices = invoices.Select(entity => (Invoice: entity,
                    ExistingPayments: GetAllPirateLikePayments(entity, cryptoCode),
                    PaymentMethodDetails: entity.GetPaymentMethod(network, PiratePaymentType.Instance)
                        .GetPaymentMethodDetails() as PirateLikeOnChainPaymentMethodDetails))
                .Select(tuple => (
                    tuple.Invoice,
                    tuple.PaymentMethodDetails,
                    ExistingPayments: tuple.ExistingPayments.Select(entity =>
                        (Payment: entity, PaymentData: (PirateLikePaymentData)entity.GetCryptoPaymentData(),
                            tuple.Invoice))
                ));

            try
            {
                _logger.LogInformation("Pirate scan {Crypto}: invoices={Count} ids=[{Ids}]", cryptoCode, expandedInvoices.Count(), string.Join(",", expandedInvoices.Select(e => e.Invoice.Id)));
            }
            catch { }

            var existingPaymentData = expandedInvoices.SelectMany(tuple => tuple.ExistingPayments);

            var accountToAddressQuery = new Dictionary<long, List<long>>();
            //create list of subaddresses to account to query the pirate wallet
            foreach (var expandedInvoice in expandedInvoices)
            {
                var addressIndexList =
                    accountToAddressQuery.GetValueOrDefault(expandedInvoice.PaymentMethodDetails.AccountIndex,
                        new List<long>());

                addressIndexList.AddRange(
                    expandedInvoice.ExistingPayments.Select(tuple => tuple.PaymentData.SubaddressIndex));
                addressIndexList.Add(expandedInvoice.PaymentMethodDetails.AddressIndex);
                accountToAddressQuery.AddOrReplace(expandedInvoice.PaymentMethodDetails.AccountIndex, addressIndexList);
            }

            try
            {
                foreach (var kv in accountToAddressQuery)
                    _logger.LogInformation("Pirate scan {Crypto}: account={Account} subaddrs=[{Subs}]", cryptoCode, kv.Key, string.Join(",", kv.Value.Distinct()));
            }
            catch { }

            var tasks = accountToAddressQuery.ToDictionary(datas => datas.Key,
                datas => pirateWalletRpcClient.SendCommandAsync<GetTransfersRequest, GetTransfersResponse>(
                    "get_transfers",
                    new GetTransfersRequest()
                    {
                        AccountIndex = datas.Key,
                        In = true,
                        SubaddrIndices = datas.Value.Distinct().ToList()
                    }));

            await Task.WhenAll(tasks.Values);


            var transferProcessingTasks = new List<Task>();

            var updatedPaymentEntities = new BlockingCollection<(PaymentEntity Payment, InvoiceEntity invoice)>();
            foreach (var keyValuePair in tasks)
            {
                var transfers = keyValuePair.Value.Result.In;
                if (transfers == null)
                {
                    continue;
                }

                transferProcessingTasks.AddRange(transfers.Select(transfer =>
                {
                    InvoiceEntity invoice = null;
                    var existingMatch = existingPaymentData.SingleOrDefault(tuple =>
                        tuple.PaymentData.Address == transfer.Address &&
                        tuple.PaymentData.TransactionId == transfer.Txid);

                    if (existingMatch.Invoice != null)
                    {
                        invoice = existingMatch.Invoice;
                    }
                    else
                    {
                        var newMatch = expandedInvoices.SingleOrDefault(tuple =>
                            tuple.PaymentMethodDetails.GetPaymentDestination() == transfer.Address);

                        if (newMatch.Invoice == null)
                        {
                            return Task.CompletedTask;
                        }

                        invoice = newMatch.Invoice;
                    }


                    return HandlePaymentData(cryptoCode, transfer.Address, transfer.Amount, transfer.SubaddrIndex.Major,
                        transfer.SubaddrIndex.Minor, transfer.Txid, transfer.Confirmations, transfer.Height, invoice,
                        updatedPaymentEntities);
                }));
            }

            transferProcessingTasks.Add(
                _paymentService.UpdatePayments(updatedPaymentEntities.Select(tuple => tuple.Item1).ToList()));
            await Task.WhenAll(transferProcessingTasks);
            foreach (var valueTuples in updatedPaymentEntities.GroupBy(entity => entity.Item2))
            {
                if (valueTuples.Any())
                {
                    _eventAggregator.Publish(new Events.InvoiceNeedUpdateEvent(valueTuples.Key.Id));
                }
            }
        }

        private async Task UpdatePaymentsForInvoice(string cryptoCode, string invoiceId)
        {
            try
            {
                var invoice = await _invoiceRepository.GetInvoice(invoiceId);
                if (invoice == null)
                    return;

                var network = _networkProvider.GetNetwork(cryptoCode);
                var pm = invoice.GetPaymentMethod(network, PiratePaymentType.Instance);
                var details = pm?.GetPaymentMethodDetails() as PirateLikeOnChainPaymentMethodDetails;
                if (details?.Activated is true)
                {
                    await UpdatePaymentStates(cryptoCode, new[] { invoice });
                }
            }
            catch (Exception ex) when (!_Cts.IsCancellationRequested)
            {
                _logger.LogError(ex, "Immediate Pirate scan for newly created invoice failed");
            }
        }


        public Task StopAsync(CancellationToken cancellationToken)
        {
            leases.Dispose();
            _Cts?.Cancel();
            return Task.CompletedTask;
        }

        private async Task OnNewBlock(string cryptoCode)
        {
            // Ask walletd to rescan and notify us on every block, then update local invoices
            try
            {
                await _pirateRpcProvider.WalletRpcClients[cryptoCode]
                    .SendCommandAsync<Empty, Empty>("request_scan", new Empty());
            }
            catch (Exception ex) when (!_Cts.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Pirate request_scan failed");
            }
            await UpdateAnyPendingPirateLikePayment(cryptoCode);
            _eventAggregator.Publish(new NewBlockEvent() { CryptoCode = cryptoCode });
        }

        private async Task OnTransactionUpdated(string cryptoCode, string transactionHash)
        {
            var paymentMethodId = new PaymentMethodId(cryptoCode, PiratePaymentType.Instance);
            GetTransferByTransactionIdResponse transfer = null;
            try
            {
                // First attempt without specifying account index
                transfer = await _pirateRpcProvider.WalletRpcClients[cryptoCode]
                    .SendCommandAsync<GetTransferByTransactionIdRequest, GetTransferByTransactionIdResponse>(
                        "get_transfer_by_txid",
                        new GetTransferByTransactionIdRequest() { TransactionId = transactionHash });
            }
            catch (System.Net.Http.HttpRequestException)
            {
                // Walletd may only expose the tx under a specific account index.
                try
                {
                    var accounts = await _pirateRpcProvider.WalletRpcClients[cryptoCode]
                        .SendCommandAsync<Empty, GetAccountsResponse>("get_accounts", new Empty());
                    foreach (var acc in accounts.SubaddressAccounts ?? Enumerable.Empty<SubaddressAccount>())
                    {
                        try
                        {
                            transfer = await _pirateRpcProvider.WalletRpcClients[cryptoCode]
                                .SendCommandAsync<GetTransferByTransactionIdRequest, GetTransferByTransactionIdResponse>(
                                    "get_transfer_by_txid",
                                    new GetTransferByTransactionIdRequest()
                                    {
                                        TransactionId = transactionHash,
                                        AccountIndex = acc.AccountIndex
                                    });
                            if (transfer != null)
                                break;
                        }
                        catch (System.Net.Http.HttpRequestException)
                        {
                            // try next account
                        }
                    }
                }
                catch
                {
                    // ignore and fall back to rescan
                }

                if (transfer == null)
                {
                    // Fall back to pending invoice rescan (periodic poll will also pick up shortly)
                    await UpdateAnyPendingPirateLikePayment(cryptoCode);
                    return;
                }
            }

            var paymentsToUpdate = new BlockingCollection<(PaymentEntity Payment, InvoiceEntity invoice)>();

            //group all destinations of the tx together and loop through the sets
            var transferItems = transfer.Transfers ??
                                 (transfer.Transfer == null
                                     ? System.Linq.Enumerable.Empty<GetTransferByTransactionIdResponse.TransferItem>()
                                     : new[] { transfer.Transfer });

            foreach (var destination in transferItems.GroupBy(destination => destination.Address))
            {
                //find the invoice corresponding to this address, else skip
                var address = destination.Key + "#" + paymentMethodId;
                var invoice = (await _invoiceRepository.GetInvoicesFromAddresses(new[] { address })).FirstOrDefault();
                if (invoice == null)
                {
                    continue;
                }

                var index = destination.First().SubaddrIndex;

                await HandlePaymentData(cryptoCode,
                    destination.Key,
                    destination.Sum(destination1 => destination1.Amount),
                    index.Major,
                    index.Minor,
                    transfer.Transfer.Txid,
                    transfer.Transfer.Confirmations,
                    transfer.Transfer.Height
                    , invoice, paymentsToUpdate);
            }

            if (paymentsToUpdate.Any())
            {
                await _paymentService.UpdatePayments(paymentsToUpdate.Select(tuple => tuple.Payment).ToList());
                foreach (var valueTuples in paymentsToUpdate.GroupBy(entity => entity.invoice))
                {
                    if (valueTuples.Any())
                    {
                        _eventAggregator.Publish(new Events.InvoiceNeedUpdateEvent(valueTuples.Key.Id));
                    }
                }
            }
        }

        private async Task HandlePaymentData(string cryptoCode, string address, long totalAmount, long subaccountIndex,
            long subaddressIndex,
            string txId, long confirmations, long blockHeight, InvoiceEntity invoice,
            BlockingCollection<(PaymentEntity Payment, InvoiceEntity invoice)> paymentsToUpdate)
        {
            //construct the payment data
            var paymentData = new PirateLikePaymentData()
            {
                Address = address,
                SubaccountIndex = subaccountIndex,
                SubaddressIndex = subaddressIndex,
                TransactionId = txId,
                ConfirmationCount = confirmations,
                Amount = totalAmount,
                BlockHeight = blockHeight,
                Network = _networkProvider.GetNetwork(cryptoCode)
            };

            //check if this tx exists as a payment to this invoice already
            var alreadyExistingPaymentThatMatches = GetAllPirateLikePayments(invoice, cryptoCode)
                .Select(entity => (Payment: entity, PaymentData: entity.GetCryptoPaymentData()))
                .SingleOrDefault(c => c.PaymentData.GetPaymentId() == paymentData.GetPaymentId());

            //if it doesnt, add it and assign a new piratelike address to the system if a balance is still due
            if (alreadyExistingPaymentThatMatches.Payment == null)
            {
                var payment = await _paymentService.AddPayment(invoice.Id, DateTimeOffset.UtcNow,
                    paymentData, _networkProvider.GetNetwork<PirateLikeSpecificBtcPayNetwork>(cryptoCode), true);
                if (payment != null)
                    await ReceivedPayment(invoice, payment);
            }
            else
            {
                //else update it with the new data
                alreadyExistingPaymentThatMatches.PaymentData = paymentData;
                alreadyExistingPaymentThatMatches.Payment.SetCryptoPaymentData(paymentData);
                paymentsToUpdate.Add((alreadyExistingPaymentThatMatches.Payment, invoice));
            }
        }

        private async Task UpdateAnyPendingPirateLikePayment(string cryptoCode)
        {
            var invoices = await _invoiceRepository.GetPendingInvoices();
            if (!invoices.Any())
                return;
            invoices = invoices.Where(entity => entity.GetPaymentMethod(new PaymentMethodId(cryptoCode, PiratePaymentType.Instance))
                ?.GetPaymentMethodDetails().Activated is true).ToArray();
            await UpdatePaymentStates(cryptoCode, invoices);
        }

        private IEnumerable<PaymentEntity> GetAllPirateLikePayments(InvoiceEntity invoice, string cryptoCode)
        {
            return invoice.GetPayments(false)
                .Where(p => p.GetPaymentMethodId() == new PaymentMethodId(cryptoCode, PiratePaymentType.Instance));
        }
    }
}
#endif
