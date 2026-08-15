#if ALTCOINS
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Filters;
using BTCPayServer.Models;
using BTCPayServer.Payments;
using BTCPayServer.Security;
using BTCPayServer.Services.Altcoins.Pirate.Configuration;
using BTCPayServer.Services.Altcoins.Pirate.Payments;
using BTCPayServer.Services.Altcoins.Pirate.RPC.Models;
using BTCPayServer.Services.Altcoins.Pirate.Services;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace BTCPayServer.Services.Altcoins.Pirate.UI
{
    [Route("stores/{storeId}/piratelike")]
    [OnlyIfSupportAttribute("ARRR")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public class UIPirateLikeStoreController : Controller
    {
        private readonly PirateLikeConfiguration _PirateLikeConfiguration;
        private readonly StoreRepository _StoreRepository;
        private readonly PirateRPCProvider _PirateRpcProvider;
        private readonly BTCPayNetworkProvider _BtcPayNetworkProvider;

        public UIPirateLikeStoreController(PirateLikeConfiguration pirateLikeConfiguration,
            StoreRepository storeRepository, PirateRPCProvider pirateRpcProvider,
            BTCPayNetworkProvider btcPayNetworkProvider)
        {
            _PirateLikeConfiguration = pirateLikeConfiguration;
            _StoreRepository = storeRepository;
            _PirateRpcProvider = pirateRpcProvider;
            _BtcPayNetworkProvider = btcPayNetworkProvider;
        }

        public StoreData StoreData => HttpContext.GetStoreData();

        [HttpGet()]
        public async Task<IActionResult> GetStorePirateLikePaymentMethods()
        {
            var pirate = StoreData.GetSupportedPaymentMethods(_BtcPayNetworkProvider)
                .OfType<PirateSupportedPaymentMethod>();

            var excludeFilters = StoreData.GetStoreBlob().GetExcludedPaymentMethods();

            var accountsList = _PirateLikeConfiguration.PirateLikeConfigurationItems.ToDictionary(pair => pair.Key,
                pair => GetAccounts(pair.Key));

            await Task.WhenAll(accountsList.Values);
            return View(new PirateLikePaymentMethodListViewModel()
            {
                Items = _PirateLikeConfiguration.PirateLikeConfigurationItems.Select(pair =>
                    GetPirateLikePaymentMethodViewModel(pirate, pair.Key, excludeFilters,
                        accountsList[pair.Key].Result))
            });
        }

        private Task<GetAccountsResponse> GetAccounts(string cryptoCode)
        {
            try
            {
                if (_PirateRpcProvider.Summaries.TryGetValue(cryptoCode, out var summary) && summary.WalletAvailable)
                {
                    return _PirateRpcProvider.WalletRpcClients[cryptoCode].SendCommandAsync<Empty, GetAccountsResponse>("get_accounts", new Empty());
                }
            }
            catch { }
            return Task.FromResult<GetAccountsResponse>(null);
        }

        private PirateLikePaymentMethodViewModel GetPirateLikePaymentMethodViewModel(
            IEnumerable<PirateSupportedPaymentMethod> pirate, string cryptoCode,
            IPaymentFilter excludeFilters, GetAccountsResponse accountsResponse)
        {
            var settings = pirate.SingleOrDefault(method => method.CryptoCode == cryptoCode);
            _PirateRpcProvider.Summaries.TryGetValue(cryptoCode, out var summary);
            _PirateLikeConfiguration.PirateLikeConfigurationItems.TryGetValue(cryptoCode,
                out var configurationItem);
            var fileAddress = Path.Combine(configurationItem.WalletDirectory, "wallet");
            var accounts = accountsResponse?.SubaddressAccounts?.Select(account =>
                new SelectListItem(
                    $"{account.AccountIndex} - {(string.IsNullOrEmpty(account.Label) ? "No label" : account.Label)} ({FormatPoolLabel(account.Pool, account.PoolPinned)})",
                    account.AccountIndex.ToString(CultureInfo.InvariantCulture)));
            return new PirateLikePaymentMethodViewModel()
            {
                WalletFileFound = System.IO.File.Exists(fileAddress),
                Enabled =
                    settings != null &&
                    !excludeFilters.Match(new PaymentMethodId(cryptoCode, PiratePaymentType.Instance)),
                Summary = summary,
                CryptoCode = cryptoCode,
                AccountIndex = settings?.AccountIndex ?? accountsResponse?.SubaddressAccounts?.FirstOrDefault()?.AccountIndex ?? 0,
                Accounts = accounts == null ? null : new SelectList(accounts, nameof(SelectListItem.Value),
                    nameof(SelectListItem.Text))
            };
        }

        private static string FormatPoolLabel(string pool, bool poolPinned)
        {
            var name = pool switch
            {
                "ironwood" => "Ironwood",
                "sapling" => "Sapling",
                _ => "Sapling"
            };
            // An unpinned account keeps following the wallet's default pool
            // automatically (e.g. it'll start using Ironwood on its own once
            // that activates), so make clear this isn't a fixed choice.
            return poolPinned ? name : $"{name}, auto";
        }

        [HttpGet("{cryptoCode}")]
        public async Task<IActionResult> GetStorePirateLikePaymentMethod(string cryptoCode)
        {
            cryptoCode = cryptoCode.ToUpperInvariant();
            if (!_PirateLikeConfiguration.PirateLikeConfigurationItems.ContainsKey(cryptoCode))
            {
                return NotFound();
            }

            var vm = GetPirateLikePaymentMethodViewModel(StoreData.GetSupportedPaymentMethods(_BtcPayNetworkProvider)
                    .OfType<PirateSupportedPaymentMethod>(), cryptoCode,
                StoreData.GetStoreBlob().GetExcludedPaymentMethods(), await GetAccounts(cryptoCode));
            return View(nameof(GetStorePirateLikePaymentMethod), vm);
        }

        [HttpPost("{cryptoCode}")]
        public async Task<IActionResult> GetStorePirateLikePaymentMethod(PirateLikePaymentMethodViewModel viewModel, string command, string cryptoCode)
        {
            cryptoCode = cryptoCode.ToUpperInvariant();
            if (!_PirateLikeConfiguration.PirateLikeConfigurationItems.TryGetValue(cryptoCode,
                out var configurationItem))
            {
                return NotFound();
            }

            if (command == "add-account")
            {
                try
                {
                    var newAccount = await _PirateRpcProvider.WalletRpcClients[cryptoCode].SendCommandAsync<CreateAccountRequest, CreateAccountResponse>("create_account", new CreateAccountRequest()
                    {
                        Label = viewModel.NewAccountLabel,
                        Pool = viewModel.NewAccountPool
                    });
                    viewModel.AccountIndex = newAccount.AccountIndex;
                }
                catch (Exception)
                {
                    ModelState.AddModelError(nameof(viewModel.AccountIndex), "Could not create a new account.");
                }

            }
            else if (command == "upload-wallet")
            {
                var valid = true;
                if (viewModel.WalletFile == null)
                {
                    ModelState.AddModelError(nameof(viewModel.WalletFile), "Please select the view-only wallet file");
                    valid = false;
                }
                if (viewModel.WalletKeysFile == null)
                {
                    ModelState.AddModelError(nameof(viewModel.WalletKeysFile), "Please select the view-only wallet keys file");
                    valid = false;
                }

                if (valid)
                {
                    if (_PirateRpcProvider.Summaries.TryGetValue(cryptoCode, out var summary))
                    {
                        if (summary.WalletAvailable)
                        {
                            TempData.SetStatusMessageModel(new StatusMessageModel()
                            {
                                Severity = StatusMessageModel.StatusSeverity.Error,
                                Message = $"There is already an active wallet configured for {cryptoCode}. Replacing it would break any existing invoices!"
                            });
                            return RedirectToAction(nameof(GetStorePirateLikePaymentMethod),
                                new { cryptoCode });
                        }
                    }

                    var fileAddress = Path.Combine(configurationItem.WalletDirectory, "wallet");
                    using (var fileStream = new FileStream(fileAddress, FileMode.Create))
                    {
                        await viewModel.WalletFile.CopyToAsync(fileStream);
                        try
                        {
                            Exec($"chmod 666 {fileAddress}");
                        }
                        catch
                        {
                        }
                    }

                    fileAddress = Path.Combine(configurationItem.WalletDirectory, "wallet.keys");
                    using (var fileStream = new FileStream(fileAddress, FileMode.Create))
                    {
                        await viewModel.WalletKeysFile.CopyToAsync(fileStream);
                        try
                        {
                            Exec($"chmod 666 {fileAddress}");
                        }
                        catch
                        {
                        }

                    }

                    fileAddress = Path.Combine(configurationItem.WalletDirectory, "password");
                    using (var fileStream = new StreamWriter(fileAddress, false))
                    {
                        await fileStream.WriteAsync(viewModel.WalletPassword);
                        try
                        {
                            Exec($"chmod 666 {fileAddress}");
                        }
                        catch
                        {
                        }
                    }

                    return RedirectToAction(nameof(GetStorePirateLikePaymentMethod), new
                    {
                        cryptoCode,
                        StatusMessage = "View-only wallet files uploaded. If they are valid the wallet will soon become available."

                    });
                }
            }

            if (!ModelState.IsValid)
            {

                var vm = GetPirateLikePaymentMethodViewModel(StoreData
                        .GetSupportedPaymentMethods(_BtcPayNetworkProvider)
                        .OfType<PirateSupportedPaymentMethod>(), cryptoCode,
                    StoreData.GetStoreBlob().GetExcludedPaymentMethods(), await GetAccounts(cryptoCode));

                vm.Enabled = viewModel.Enabled;
                vm.NewAccountLabel = viewModel.NewAccountLabel;
                vm.NewAccountPool = viewModel.NewAccountPool;
                vm.AccountIndex = viewModel.AccountIndex;
                return View(vm);
            }

            var storeData = StoreData;
            var blob = storeData.GetStoreBlob();
            storeData.SetSupportedPaymentMethod(new PirateSupportedPaymentMethod()
            {
                AccountIndex = viewModel.AccountIndex,
                CryptoCode = viewModel.CryptoCode
            });

            blob.SetExcluded(new PaymentMethodId(viewModel.CryptoCode, PiratePaymentType.Instance), !viewModel.Enabled);
            storeData.SetStoreBlob(blob);
            await _StoreRepository.UpdateStore(storeData);
            return RedirectToAction("GetStorePirateLikePaymentMethods",
                new { StatusMessage = $"{cryptoCode} settings updated successfully", storeId = StoreData.Id });
        }

        private void Exec(string cmd)
        {

            var escapedArgs = cmd.Replace("\"", "\\\"", StringComparison.InvariantCulture);

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    FileName = "/bin/sh",
                    Arguments = $"-c \"{escapedArgs}\""
                }
            };

#pragma warning disable CA1416 // Validate platform compatibility
            process.Start();
#pragma warning restore CA1416 // Validate platform compatibility
            process.WaitForExit();
        }

        public class PirateLikePaymentMethodListViewModel
        {
            public IEnumerable<PirateLikePaymentMethodViewModel> Items { get; set; }
        }

        public class PirateLikePaymentMethodViewModel
        {
            public PirateRPCProvider.PirateLikeSummary Summary { get; set; }
            public string CryptoCode { get; set; }
            public string NewAccountLabel { get; set; }

            /// <summary>"sapling" or "ironwood"; which pool a newly created account will use.</summary>
            public string NewAccountPool { get; set; } = "sapling";
            public long AccountIndex { get; set; }
            public bool Enabled { get; set; }

            public IEnumerable<SelectListItem> Accounts { get; set; }
            public bool WalletFileFound { get; set; }
            [Display(Name = "View-Only Wallet File")]
            public IFormFile WalletFile { get; set; }
            public IFormFile WalletKeysFile { get; set; }
            public string WalletPassword { get; set; }
        }
    }
}
#endif
