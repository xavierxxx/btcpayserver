﻿using BTCPayServer.Data;
using BTCPayServer.Filters;
using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using NBitcoin;
using NBitpayClient;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using BTCPayServer.Services.Rates;
using System.Net.WebSockets;
using System.Threading;
using BTCPayServer.Events;
using NBXplorer;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;

namespace BTCPayServer.Controllers
{
    public partial class InvoiceController
    {
        [HttpGet]
        [Route("invoices/{invoiceId}")]
        public async Task<IActionResult> Invoice(string invoiceId)
        {
            var invoice = (await _InvoiceRepository.GetInvoices(new InvoiceQuery()
            {
                UserId = GetUserId(),
                InvoiceId = invoiceId,
                IncludeAddresses = true,
                IncludeEvents = true
            })).FirstOrDefault();
            if (invoice == null)
                return NotFound();

            var dto = invoice.EntityToDTO(_NetworkProvider);
            var store = await _StoreRepository.FindStore(invoice.StoreId);

            InvoiceDetailsModel model = new InvoiceDetailsModel()
            {
                StoreName = store.StoreName,
                StoreLink = Url.Action(nameof(StoresController.UpdateStore), "Stores", new { storeId = store.Id }),
                Id = invoice.Id,
                Status = invoice.Status,
                TransactionSpeed = invoice.SpeedPolicy == SpeedPolicy.HighSpeed ? "high" : invoice.SpeedPolicy == SpeedPolicy.MediumSpeed ? "medium" : "low",
                RefundEmail = invoice.RefundMail,
                CreatedDate = invoice.InvoiceTime,
                ExpirationDate = invoice.ExpirationTime,
                MonitoringDate = invoice.MonitoringExpiration,
                OrderId = invoice.OrderId,
                BuyerInformation = invoice.BuyerInformation,
                Fiat = FormatCurrency((decimal)dto.Price, dto.Currency),
                NotificationUrl = invoice.NotificationURL,
                RedirectUrl = invoice.RedirectURL,
                ProductInformation = invoice.ProductInformation,
                StatusException = invoice.ExceptionStatus,
                Events = invoice.Events
            };

            foreach (var data in invoice.GetPaymentMethods(null))
            {
                var cryptoInfo = dto.CryptoInfo.First(o => o.GetpaymentMethodId() == data.GetId());
                var accounting = data.Calculate();
                var paymentMethodId = data.GetId();
                var cryptoPayment = new InvoiceDetailsModel.CryptoPayment();
                cryptoPayment.PaymentMethod = ToString(paymentMethodId);
                cryptoPayment.Due = accounting.Due.ToString() + $" {paymentMethodId.CryptoCode}";
                cryptoPayment.Paid = accounting.CryptoPaid.ToString() + $" {paymentMethodId.CryptoCode}";

                var onchainMethod = data.GetPaymentMethodDetails() as Payments.Bitcoin.BitcoinLikeOnChainPaymentMethod;
                if (onchainMethod != null)
                {
                    cryptoPayment.Address = onchainMethod.DepositAddress;
                }
                cryptoPayment.Rate = FormatCurrency(data);
                cryptoPayment.PaymentUrl = cryptoInfo.PaymentUrls.BIP21;
                model.CryptoPayments.Add(cryptoPayment);
            }

            var onChainPayments = invoice
                .GetPayments()
                .Select<PaymentEntity, Task<object>>(async payment =>
                {
                    var paymentNetwork = _NetworkProvider.GetNetwork(payment.GetCryptoCode());
                    var paymentData = payment.GetCryptoPaymentData();
                    if (paymentData is Payments.Bitcoin.BitcoinLikePaymentData onChainPaymentData)
                    {
                        var m = new InvoiceDetailsModel.Payment();
                        m.Crypto = payment.GetPaymentMethodId().CryptoCode;
                        m.DepositAddress = onChainPaymentData.Output.ScriptPubKey.GetDestinationAddress(paymentNetwork.NBitcoinNetwork);

                        int confirmationCount = 0;
                        if ((onChainPaymentData.ConfirmationCount < paymentNetwork.MaxTrackedConfirmation && payment.Accounted)
                             && (onChainPaymentData.Legacy || invoice.MonitoringExpiration < DateTimeOffset.UtcNow)) // The confirmation count in the paymentData is not up to date
                        {
                            confirmationCount = (await ((ExplorerClientProvider)_ServiceProvider.GetService(typeof(ExplorerClientProvider))).GetExplorerClient(payment.GetCryptoCode())?.GetTransactionAsync(onChainPaymentData.Outpoint.Hash))?.Confirmations ?? 0;
                            onChainPaymentData.ConfirmationCount = confirmationCount;
                            payment.SetCryptoPaymentData(onChainPaymentData);
                            await _InvoiceRepository.UpdatePayments(new List<PaymentEntity> { payment });
                        }
                        else
                        {
                            confirmationCount = onChainPaymentData.ConfirmationCount;
                        }
                        if (confirmationCount >= paymentNetwork.MaxTrackedConfirmation)
                        {
                            m.Confirmations = "At least " + (paymentNetwork.MaxTrackedConfirmation);
                        }
                        else
                        {
                            m.Confirmations = confirmationCount.ToString(CultureInfo.InvariantCulture);
                        }

                        m.TransactionId = onChainPaymentData.Outpoint.Hash.ToString();
                        m.ReceivedTime = payment.ReceivedTime;
                        m.TransactionLink = string.Format(CultureInfo.InvariantCulture, paymentNetwork.BlockExplorerLink, m.TransactionId);
                        m.Replaced = !payment.Accounted;
                        return m;
                    }
                    else
                    {
                        var lightningPaymentData = (Payments.Lightning.LightningLikePaymentData)paymentData;
                        return new InvoiceDetailsModel.OffChainPayment()
                        {
                            Crypto = paymentNetwork.CryptoCode,
                            BOLT11 = lightningPaymentData.BOLT11
                        };
                    }
                })
                .ToArray();
            await Task.WhenAll(onChainPayments);
            model.Addresses = invoice.HistoricalAddresses.Select(h => new InvoiceDetailsModel.AddressModel
            {
                Destination = h.GetAddress(),
                PaymentMethod = ToString(h.GetPaymentMethodId()),
                Current = !h.UnAssigned.HasValue
            }).ToArray();
            model.OnChainPayments = onChainPayments.Select(p => p.GetAwaiter().GetResult()).OfType<InvoiceDetailsModel.Payment>().ToList();
            model.OffChainPayments = onChainPayments.Select(p => p.GetAwaiter().GetResult()).OfType<InvoiceDetailsModel.OffChainPayment>().ToList();
            model.StatusMessage = StatusMessage;
            return View(model);
        }

        private string ToString(PaymentMethodId paymentMethodId)
        {
            var type = paymentMethodId.PaymentType.ToString();
            switch (paymentMethodId.PaymentType)
            {
                case PaymentTypes.BTCLike:
                    type = "On-Chain";
                    break;
                case PaymentTypes.LightningLike:
                    type = "Off-Chain";
                    break;
            }
            return $"{paymentMethodId.CryptoCode} ({type})";
        }

        [HttpGet]
        [Route("i/{invoiceId}")]
        [Route("i/{invoiceId}/{paymentMethodId}")]
        [Route("invoice")]
        [AcceptMediaTypeConstraint("application/bitcoin-paymentrequest", false)]
        [XFrameOptionsAttribute(null)]
        public async Task<IActionResult> Checkout(string invoiceId, string id = null, string paymentMethodId = null)
        {
            //Keep compatibility with Bitpay
            invoiceId = invoiceId ?? id;
            id = invoiceId;
            ////

            var model = await GetInvoiceModel(invoiceId, paymentMethodId);
            if (model == null)
                return NotFound();

            return View(nameof(Checkout), model);
        }

        private async Task<PaymentModel> GetInvoiceModel(string invoiceId, string paymentMethodIdStr)
        {
            var invoice = await _InvoiceRepository.GetInvoice(null, invoiceId);
            if (invoice == null)
                return null;
            var store = await _StoreRepository.FindStore(invoice.StoreId);
            bool isDefaultCrypto = false;
            if (paymentMethodIdStr == null)
            {
                paymentMethodIdStr = store.GetDefaultCrypto();
                isDefaultCrypto = true;
            }

            var paymentMethodId = PaymentMethodId.Parse(paymentMethodIdStr);
            var network = _NetworkProvider.GetNetwork(paymentMethodId.CryptoCode);
            if (invoice == null || network == null)
                return null;
            if (!invoice.Support(paymentMethodId))
            {
                if (!isDefaultCrypto)
                    return null;
                var paymentMethodTemp = invoice.GetPaymentMethods(_NetworkProvider).First();
                network = paymentMethodTemp.Network;
                paymentMethodId = paymentMethodTemp.GetId();
            }

            var paymentMethod = invoice.GetPaymentMethod(paymentMethodId, _NetworkProvider);
            var paymentMethodDetails = paymentMethod.GetPaymentMethodDetails();
            var dto = invoice.EntityToDTO(_NetworkProvider);
            var cryptoInfo = dto.CryptoInfo.First(o => o.GetpaymentMethodId() == paymentMethodId);
            var storeBlob = store.GetStoreBlob();
            var currency = invoice.ProductInformation.Currency;
            var accounting = paymentMethod.Calculate();
            var model = new PaymentModel()
            {
                CryptoCode = network.CryptoCode,
                PaymentMethodId = paymentMethodId.ToString(),
                IsLightning = paymentMethodId.PaymentType == PaymentTypes.LightningLike,
                ServerUrl = HttpContext.Request.GetAbsoluteRoot(),
                OrderId = invoice.OrderId,
                InvoiceId = invoice.Id,
                DefaultLang = storeBlob.DefaultLang ?? "en-US",
                CustomCSSLink = storeBlob.CustomCSS?.AbsoluteUri,
                CustomLogoLink = storeBlob.CustomLogo?.AbsoluteUri,
                BtcAddress = paymentMethodDetails.GetPaymentDestination(),
                OrderAmount = (accounting.TotalDue - accounting.NetworkFee).ToString(),
                BtcDue = accounting.Due.ToString(),
                CustomerEmail = invoice.RefundMail,
                ExpirationSeconds = Math.Max(0, (int)(invoice.ExpirationTime - DateTimeOffset.UtcNow).TotalSeconds),
                MaxTimeSeconds = (int)(invoice.ExpirationTime - invoice.InvoiceTime).TotalSeconds,
                MaxTimeMinutes = (int)(invoice.ExpirationTime - invoice.InvoiceTime).TotalMinutes,
                ItemDesc = invoice.ProductInformation.ItemDesc,
                Rate = FormatCurrency(paymentMethod),
                MerchantRefLink = invoice.RedirectURL ?? "/",
                StoreName = store.StoreName,
                InvoiceBitcoinUrl = paymentMethodId.PaymentType == PaymentTypes.BTCLike ? cryptoInfo.PaymentUrls.BIP21 :
                                    paymentMethodId.PaymentType == PaymentTypes.LightningLike ? cryptoInfo.PaymentUrls.BOLT11 :
                                    throw new NotSupportedException(),
                PeerInfo = (paymentMethodDetails as LightningLikePaymentMethodDetails)?.NodeInfo,
                InvoiceBitcoinUrlQR = paymentMethodId.PaymentType == PaymentTypes.BTCLike ? cryptoInfo.PaymentUrls.BIP21 :
                                    paymentMethodId.PaymentType == PaymentTypes.LightningLike ? cryptoInfo.PaymentUrls.BOLT11.ToUpperInvariant() :
                                    throw new NotSupportedException(),
                TxCount = accounting.TxRequired,
                BtcPaid = accounting.Paid.ToString(),
                Status = invoice.Status,
                CryptoImage = "/" + GetImage(paymentMethodId, network),
                NetworkFee = paymentMethodDetails.GetTxFee(),
                IsMultiCurrency = invoice.GetPayments().Select(p => p.GetPaymentMethodId()).Concat(new[] { paymentMethod.GetId() }).Distinct().Count() > 1,
                AllowCoinConversion = storeBlob.AllowCoinConversion,
                AvailableCryptos = invoice.GetPaymentMethods(_NetworkProvider)
                                          .Where(i => i.Network != null)
                                          .Select(kv => new PaymentModel.AvailableCrypto()
                                          {
                                              PaymentMethodId = kv.GetId().ToString(),
                                              CryptoImage = "/" + GetImage(kv.GetId(), kv.Network),
                                              Link = Url.Action(nameof(Checkout), new { invoiceId = invoiceId, paymentMethodId = kv.GetId().ToString() })
                                          }).Where(c => c.CryptoImage != "/")
                .ToList()
            };

            var expiration = TimeSpan.FromSeconds(model.ExpirationSeconds);
            model.TimeLeft = expiration.PrettyPrint();
            return model;
        }

        private string GetImage(PaymentMethodId paymentMethodId, BTCPayNetwork network)
        {
            return (paymentMethodId.PaymentType == PaymentTypes.BTCLike ? Url.Content(network.CryptoImagePath) : Url.Content(network.LightningImagePath));
        }

        private string FormatCurrency(PaymentMethod paymentMethod)
        {
            string currency = paymentMethod.ParentEntity.ProductInformation.Currency;
            return FormatCurrency(paymentMethod.Rate, currency);
        }
        public string FormatCurrency(decimal price, string currency)
        {
            return price.ToString("C", _CurrencyNameTable.GetCurrencyProvider(currency)) + $" ({currency})";
        }

        [HttpGet]
        [Route("i/{invoiceId}/status")]
        [Route("i/{invoiceId}/{paymentMethodId}/status")]
        public async Task<IActionResult> GetStatus(string invoiceId, string paymentMethodId = null)
        {
            var model = await GetInvoiceModel(invoiceId, paymentMethodId);
            if (model == null)
                return NotFound();
            return Json(model);
        }

        [HttpGet]
        [Route("i/{invoiceId}/status/ws")]
        public async Task<IActionResult> GetStatusWebSocket(string invoiceId)
        {
            if (!HttpContext.WebSockets.IsWebSocketRequest)
                return NotFound();
            var invoice = await _InvoiceRepository.GetInvoice(null, invoiceId);
            if (invoice == null || invoice.Status == "complete" || invoice.Status == "invalid" || invoice.Status == "expired")
                return NotFound();
            var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
            CompositeDisposable leases = new CompositeDisposable();
            try
            {
                leases.Add(_EventAggregator.Subscribe<Events.InvoiceDataChangedEvent>(async o => await NotifySocket(webSocket, o.InvoiceId, invoiceId)));
                leases.Add(_EventAggregator.Subscribe<Events.InvoiceNewAddressEvent>(async o => await NotifySocket(webSocket, o.InvoiceId, invoiceId)));
                leases.Add(_EventAggregator.Subscribe<Events.InvoiceEvent>(async o => await NotifySocket(webSocket, o.InvoiceId, invoiceId)));
                while (true)
                {
                    var message = await webSocket.ReceiveAsync(DummyBuffer, default(CancellationToken));
                    if (message.MessageType == WebSocketMessageType.Close)
                        break;
                }
            }
            finally
            {
                leases.Dispose();
                await webSocket.CloseSocket();
            }
            return new EmptyResult();
        }

        ArraySegment<Byte> DummyBuffer = new ArraySegment<Byte>(new Byte[1]);
        private async Task NotifySocket(WebSocket webSocket, string invoiceId, string expectedId)
        {
            if (invoiceId != expectedId || webSocket.State != WebSocketState.Open)
                return;
            CancellationTokenSource cts = new CancellationTokenSource();
            cts.CancelAfter(5000);
            try
            {
                await webSocket.SendAsync(DummyBuffer, WebSocketMessageType.Binary, true, cts.Token);
            }
            catch { try { webSocket.Dispose(); } catch { } }
        }

        [HttpPost]
        [Route("i/{invoiceId}/UpdateCustomer")]
        public async Task<IActionResult> UpdateCustomer(string invoiceId, [FromBody]UpdateCustomerModel data)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }
            await _InvoiceRepository.UpdateInvoice(invoiceId, data).ConfigureAwait(false);
            return Ok();
        }

        [HttpGet]
        [Route("invoices")]
        [Authorize(AuthenticationSchemes = "Identity.Application")]
        [BitpayAPIConstraint(false)]
        public async Task<IActionResult> ListInvoices(string searchTerm = null, int skip = 0, int count = 50)
        {
            var model = new InvoicesModel();
            var filterString = new SearchString(searchTerm);
            foreach (var invoice in await _InvoiceRepository.GetInvoices(new InvoiceQuery()
            {
                TextSearch = filterString.TextSearch,
                Count = count,
                Skip = skip,
                UserId = GetUserId(),
                Status = filterString.Filters.TryGet("status"),
                StoreId = filterString.Filters.TryGet("storeid")
            }))
            {
                model.SearchTerm = searchTerm;
                model.Invoices.Add(new InvoiceModel()
                {
                    Status = invoice.Status,
                    Date = (DateTimeOffset.UtcNow - invoice.InvoiceTime).Prettify() + " ago",
                    InvoiceId = invoice.Id,
                    OrderId = invoice.OrderId ?? string.Empty,
                    RedirectUrl = invoice.RedirectURL ?? string.Empty,
                    AmountCurrency = $"{invoice.ProductInformation.Price.ToString(CultureInfo.InvariantCulture)} {invoice.ProductInformation.Currency}"
                });
            }
            model.Skip = skip;
            model.Count = count;
            model.StatusMessage = StatusMessage;
            return View(model);
        }

        [HttpGet]
        [Route("invoices/create")]
        [Authorize(AuthenticationSchemes = "Identity.Application")]
        [BitpayAPIConstraint(false)]
        public async Task<IActionResult> CreateInvoice()
        {
            var stores = await GetStores(GetUserId());
            if (stores.Count() == 0)
            {
                StatusMessage = "Error: You need to create at least one store before creating a transaction";
                return RedirectToAction(nameof(UserStoresController.ListStores), "UserStores");
            }
            return View(new CreateInvoiceModel() { Stores = stores });
        }

        [HttpPost]
        [Route("invoices/create")]
        [Authorize(AuthenticationSchemes = "Identity.Application")]
        [BitpayAPIConstraint(false)]
        public async Task<IActionResult> CreateInvoice(CreateInvoiceModel model)
        {
            model.Stores = await GetStores(GetUserId(), model.StoreId);
            if (!ModelState.IsValid)
            {
                return View(model);
            }
            var store = await _StoreRepository.FindStore(model.StoreId, GetUserId());
            StatusMessage = null;
            if (store.Role != StoreRoles.Owner)
            {
                ModelState.AddModelError(nameof(model.StoreId), "You need to be owner of this store to create an invoice");
                return View(model);
            }

            if (store.GetSupportedPaymentMethods(_NetworkProvider).Count() == 0)
            {
                ModelState.AddModelError(nameof(model.StoreId), "You need to configure the derivation scheme in order to create an invoice");
                return View(model);
            }

            if (StatusMessage != null)
            {
                return RedirectToAction(nameof(StoresController.UpdateStore), "Stores", new
                {
                    storeId = store.Id
                });
            }

            try
            {
                var result = await CreateInvoiceCore(new Invoice()
                {
                    Price = model.Amount.Value,
                    Currency = model.Currency,
                    PosData = model.PosData,
                    OrderId = model.OrderId,
                    //RedirectURL = redirect + "redirect",
                    NotificationURL = model.NotificationUrl,
                    ItemDesc = model.ItemDesc,
                    FullNotifications = true,
                    BuyerEmail = model.BuyerEmail,
                }, store, HttpContext.Request.GetAbsoluteRoot());

                StatusMessage = $"Invoice {result.Data.Id} just created!";
                return RedirectToAction(nameof(ListInvoices));
            }
            catch (RateUnavailableException)
            {
                ModelState.TryAddModelError(nameof(model.Currency), "Unsupported currency");
                return View(model);
            }
        }

        private async Task<SelectList> GetStores(string userId, string storeId = null)
        {
            return new SelectList(await _StoreRepository.GetStoresByUserId(userId), nameof(StoreData.Id), nameof(StoreData.StoreName), storeId);
        }

        [HttpPost]
        [Authorize(AuthenticationSchemes = "Identity.Application")]
        [BitpayAPIConstraint(false)]
        public IActionResult SearchInvoice(InvoicesModel invoices)
        {
            return RedirectToAction(nameof(ListInvoices), new
            {
                searchTerm = invoices.SearchTerm,
                skip = invoices.Skip,
                count = invoices.Count,
            });
        }

        [HttpPost]
        [Route("invoices/invalidatepaid")]
        [Authorize(AuthenticationSchemes = "Identity.Application")]
        [BitpayAPIConstraint(false)]
        public async Task<IActionResult> InvalidatePaidInvoice(string invoiceId)
        {
            await _InvoiceRepository.UpdatePaidInvoiceToInvalid(invoiceId);
            _EventAggregator.Publish(new InvoiceEvent(invoiceId, 1008, "invoice_markedInvalid"));
            return RedirectToAction(nameof(ListInvoices));
        }

        [TempData]
        public string StatusMessage
        {
            get;
            set;
        }

        private string GetUserId()
        {
            return _UserManager.GetUserId(User);
        }
    }
}
