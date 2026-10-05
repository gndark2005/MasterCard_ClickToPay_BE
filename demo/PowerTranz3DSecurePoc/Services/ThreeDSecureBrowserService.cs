using System.Text.Json;
using System.Threading.Channels;
using System.Web;
using Microsoft.Playwright;
using PowerTranz3DSecurePoc.Demo;
using PowerTranz3DSecurePoc.Models;
using PowerTranz3DSecurePoc.Shop;

namespace PowerTranz3DSecurePoc.Services;

public class ThreeDSecureException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The demo window was closed; ends the demo instead of being reported as a failed payment.</summary>
public sealed class BrowserClosedException(string message) : ThreeDSecureException(message);

/// <summary>The shopper went back to the cart or switched payment method while the attempt was in progress.</summary>
public sealed class PaymentCancelledException() : ThreeDSecureException("Payment attempt cancelled by the shopper.");

/// <summary>What the browser posted to MerchantResponseUrl at the end of the 3DS flow.</summary>
public sealed record ThreeDSecureCallback(
    string Url,
    string Method,
    IReadOnlyList<string> FieldNames,
    SaleResponse? Response,
    string? ParseError);

/// <summary>
/// A payment started on the checkout page: the checkout form's card and billing details, (Card null) a
/// Hosted Payment Page payment where PowerTranz collects them, or a completed Click to Pay checkout.
/// </summary>
public sealed record PaymentRequest(CardOptions? Card, SaleBillingAddress? Billing, ClickToPayCheckout? ClickToPay = null);

/// <summary>
/// Drives the demo in a real Chromium window: the cart, then the checkout page, which stays loaded while the
/// shopper retries. Each attempt's Sale RedirectData runs in the checkout page's iframe (3DS, or the Hosted
/// Payment Page) until it reaches MerchantResponseUrl. Headless runs load the RedirectData as the whole page.
/// </summary>
public sealed class ThreeDSecureBrowserService(PowerTranzOptions options, ConsoleRecorder recorder, ClickToPayOptions clickToPay)
    : IAsyncDisposable
{
    private const string ChromiumMissingMarker = "Executable doesn't exist";
    // Origin the checkout page and form are served from. Playwright answers it locally; nothing listens there.
    private static readonly Uri DemoOrigin = new("http://localhost:5500/");
    private static readonly string FormFile = Path.Combine(AppContext.BaseDirectory, "hpp", "Checkout.html");

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;
    // Console line where the current order starts, so the checkout console shows only this order's calls.
    private int _orderLogStart;
    private string _checkoutHtml = "";
    private string _clickToPayHtml = "";
    private readonly TaskCompletionSource _closedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<string> _checkoutTcs = NewPromptTcs();
    // Payments started on the checkout page; null means "back to cart".
    private Channel<PaymentRequest?> _payments = Channel.CreateUnbounded<PaymentRequest?>();
    // Recreated for every payment attempt.
    private TaskCompletionSource<ThreeDSecureCallback> _callbackTcs = NewCallbackTcs();
    // Cancelled by "back" / "cancel" from the checkout page; recreated for every payment attempt.
    private CancellationTokenSource _attemptCts = new();

    public bool IsOpen => _page is { IsClosed: false } && _browser is { IsConnected: true };

    /// <summary>Launches Chromium once; later calls reuse the same window.</summary>
    public async Task StartAsync()
    {
        if (_page is not null) return;

        _playwright = await CreatePlaywrightAsync();
        _browser = await LaunchChromiumAsync(_playwright);
        var context = await _browser.NewContextAsync(new BrowserNewContextOptions { ViewportSize = ViewportSize.NoViewport });

        // Route at context level so it also covers iframes and any popup window the ACS may open.
        await context.RouteAsync(IsMerchantResponseUrl, HandleMerchantResponseAsync);
        await context.RouteAsync(url => url.StartsWith(DemoOrigin.ToString(), StringComparison.OrdinalIgnoreCase), ServeDemoOriginAsync);

        _page = await context.NewPageAsync();
        AttachDiagnostics(_page);
        context.Page += (_, popup) =>
        {
            Console.WriteLine("[browser] New window opened.");
            AttachDiagnostics(popup);
        };
        _page.Close += (_, _) => _closedTcs.TrySetResult();
        _browser.Disconnected += (_, _) => _closedTcs.TrySetResult();

        await _page.ExposeFunctionAsync<string>(CartPage.CheckoutFunctionName, json => _checkoutTcs.TrySetResult(json));
        await _page.ExposeFunctionAsync<string>(CheckoutPage.ActionFunctionName, OnCheckoutAction);
        await _page.ExposeFunctionAsync<int, string>(CheckoutPage.PullLogsFunctionName, PullLogs);
    }

    private static TaskCompletionSource<string> NewPromptTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<ThreeDSecureCallback> NewCallbackTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Shows the shop cart and waits for "Check out". The order is priced from the server-side catalog.
    /// <paramref name="previous"/> restores the cart when the user comes back from the checkout page.
    /// </summary>
    public async Task<Order> PromptCheckoutAsync(ShopOptions shop, Order? previous, CancellationToken cancellationToken)
    {
        await StartAsync();
        _checkoutTcs = NewPromptTcs();
        Console.WriteLine("Waiting for checkout in the browser (cart page)...");
        var quantities = previous?.Lines.ToDictionary(l => l.Product.Id, l => l.Quantity);
        await _page!.SetContentAsync(CartPage.Render(shop, quantities));

        var finished = await Task.WhenAny(_checkoutTcs.Task, _closedTcs.Task, Task.Delay(Timeout.Infinite, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        if (finished == _closedTcs.Task)
            throw new BrowserClosedException("Browser was closed.");

        List<CartItemInput> items;
        try
        {
            items = JsonSerializer.Deserialize<List<CartItemInput>>(await _checkoutTcs.Task, PowerTranzService.JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            throw new ThreeDSecureException($"Cart page returned invalid data: {ex.Message}", ex);
        }

        Order order;
        try
        {
            order = OrderPricing.Price(items, shop);
        }
        catch (InvalidOperationException ex)
        {
            throw new ThreeDSecureException($"Checkout rejected: {ex.Message}", ex);
        }

        _orderLogStart = recorder.Snapshot().Count;
        Console.WriteLine("Checkout:");
        foreach (var line in order.Lines)
            Console.WriteLine($"  {line.Quantity} x {line.Product.Name} @ {line.Product.Price:0.00} = {line.LineTotal:0.00}");
        Console.WriteLine($"  Order total: {order.Total:0.00} (subtotal {order.Subtotal:0.00} + shipping {order.Shipping:0.00})");
        return order;
    }

    /// <summary>
    /// Shows the checkout page: the HPP checkout template in an iframe (or, with a Hosted Payment Page configured,
    /// a button that starts it), the order summary and the live console.
    /// </summary>
    /// <param name="clickToPayCurrency">ISO alphabetic currency for Click to Pay; null hides the Click to Pay method.</param>
    public async Task ShowCheckoutAsync(Order order, ShopOptions shop, string currencyCode, bool hostedPage,
        string? clickToPayCurrency)
    {
        await StartAsync();
        _payments = Channel.CreateUnbounded<PaymentRequest?>();
        var withClickToPay = clickToPay.Enabled && clickToPayCurrency is not null;
        _checkoutHtml = CheckoutPage.Render(order, shop, currencyCode, hostedPage, withClickToPay, _orderLogStart);
        _clickToPayHtml = withClickToPay ? ClickToPayPage.Render(clickToPay, order.Total, clickToPayCurrency!) : "";
        await _page!.GotoAsync(new Uri(DemoOrigin, CheckoutPage.PagePath.TrimStart('/')).ToString());
        Console.WriteLine(hostedPage
            ? "Checkout page ready (card entry in the PowerTranz Hosted Payment Page)."
            : "Checkout page ready (HPP checkout template; its POST is captured by the merchant app).");
    }

    /// <summary>Waits for a payment on the checkout page. Returns null when the shopper goes back to the cart.</summary>
    public async Task<PaymentRequest?> WaitForPaymentAsync(CancellationToken cancellationToken)
    {
        var read = _payments.Reader.ReadAsync(cancellationToken).AsTask();
        if (await Task.WhenAny(read, _closedTcs.Task) == _closedTcs.Task)
            throw new BrowserClosedException("Browser was closed.");

        var request = await read;
        if (request is null)
        {
            Console.WriteLine("Back to cart.");
            return null;
        }
        // Not disposed: the checkout page may still cancel the previous token from its callback thread.
        _attemptCts = new CancellationTokenSource();
        await SetBusyAsync(request.ClickToPay is null
            ? "Creating the Sale with PowerTranz…"
            : "Completing the Click to Pay checkout with Mastercard…");
        return request;
    }

    private void OnCheckoutAction(string json)
    {
        try
        {
            var action = JsonSerializer.Deserialize<CheckoutAction>(json, PowerTranzService.JsonOptions);
            switch (action?.Action)
            {
                case "back":
                    _attemptCts.Cancel();
                    _payments.Writer.TryWrite(null);
                    break;
                case "cancel":
                    _attemptCts.Cancel();
                    break;
                case "clickToPayLog":
                    Console.WriteLine($"[Click to Pay] {action.Text}");
                    break;
                case "clickToPay" when !string.IsNullOrWhiteSpace(action.CorrelationId)
                                       && !string.IsNullOrWhiteSpace(action.MerchantTransactionId):
                    Console.WriteLine();
                    Console.WriteLine($"[Click to Pay] checkoutWithCard() COMPLETE. srcCorrelationId={action.CorrelationId}");
                    _payments.Writer.TryWrite(new PaymentRequest(null, null,
                        new ClickToPayCheckout(action.CorrelationId, action.MerchantTransactionId, action.FlowId)));
                    break;
                case "clickToPay":
                    Console.WriteLine("[warn] Click to Pay page sent no correlationId / merchantTransactionId.");
                    break;
                default:
                    _payments.Writer.TryWrite(new PaymentRequest(null, null));
                    break;
            }
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"[warn] Checkout page sent invalid data: {ex.Message}");
        }
    }

    private sealed record CheckoutAction(
        string? Action = null,
        string? Text = null,
        string? CorrelationId = null,
        string? MerchantTransactionId = null,
        string? FlowId = null);

    private async Task SetBusyAsync(string text)
    {
        try
        {
            await _page!.EvaluateAsync("t => window.setBusy && window.setBusy(true, t)", text);
        }
        catch (PlaywrightException)
        {
            // Status text only.
        }
    }

    // Serves the checkout page and the unmodified HPP template, and captures the template's POST.
    private async Task ServeDemoOriginAsync(IRoute route)
    {
        var request = route.Request;
        var path = new Uri(request.Url).AbsolutePath;
        try
        {
            if (path.Equals(CheckoutPage.FormPath, StringComparison.OrdinalIgnoreCase) && request.Method == "POST")
            {
                // The template's form has action="", so it posts back to the URL it was loaded from. With a real
                // Hosted Payment Page that URL is PowerTranz; here the merchant app receives it instead.
                _payments.Writer.TryWrite(ParseCheckoutForm(request.PostData));
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType = "text/html; charset=utf-8",
                    Body = "<!DOCTYPE html><p style=\"font:15px 'Segoe UI',sans-serif;color:#0b645f;padding:16px\">Processing your payment…</p>",
                });
                return;
            }

            if (path.Equals(CheckoutPage.FormPath, StringComparison.OrdinalIgnoreCase))
            {
                await route.FulfillAsync(new RouteFulfillOptions { Status = 200, ContentType = "text/html; charset=utf-8", Path = FormFile });
                return;
            }

            if (path.Equals(ClickToPayPage.PagePath, StringComparison.OrdinalIgnoreCase) && _clickToPayHtml.Length > 0)
            {
                await route.FulfillAsync(new RouteFulfillOptions { Status = 200, ContentType = "text/html; charset=utf-8", Body = _clickToPayHtml });
                return;
            }

            if (path.Equals(CheckoutPage.PagePath, StringComparison.OrdinalIgnoreCase))
            {
                await route.FulfillAsync(new RouteFulfillOptions { Status = 200, ContentType = "text/html; charset=utf-8", Body = _checkoutHtml });
                return;
            }

            await route.FulfillAsync(new RouteFulfillOptions { Status = 404 });
        }
        catch (PlaywrightException ex)
        {
            Console.WriteLine($"[warn] Could not serve {path}: {ex.Message}");
        }
    }

    // Maps the HPP template's fields (see powertranz/hpp/Checkout.html) to the Sale's Source and BillingAddress.
    private static PaymentRequest ParseCheckoutForm(string? postData)
    {
        var form = HttpUtility.ParseQueryString(postData ?? "");
        string? Field(string name) => string.IsNullOrWhiteSpace(form[name]) ? null : form[name]!.Trim();

        // The template asks for MMYY; the PowerTranz API expects YYMM.
        var expiry = new string((Field("CardExpDate") ?? "").Where(char.IsDigit).ToArray());
        var card = new CardOptions
        {
            Pan = new string((Field("CardNo") ?? "").Where(char.IsDigit).ToArray()),
            Expiration = expiry.Length == 4 ? expiry[2..] + expiry[..2] : expiry,
            Cvv = Field("CardCVV2") ?? "",
            HolderName = Field("CardholderName") ?? "",
        };
        // The template's selects hold ISO codes: numeric country ("840") and "US-NY" subdivisions.
        var state = Field("BillToState");
        var billing = new SaleBillingAddress
        {
            FirstName = Field("BillToFirstName"),
            LastName = Field("BillToLastName"),
            Line1 = Field("BillToAddress1"),
            Line2 = Field("BillToAddress2"),
            State = state?.Contains('-') == true ? state[(state.IndexOf('-') + 1)..] : state,
            PostalCode = Field("BillToPostCode"),
            CountryCode = Field("BillToCountry"),
            EmailAddress = Field("BillToEmail"),
            PhoneNumber = Field("BillToTelephone"),
        };

        Console.WriteLine();
        Console.WriteLine($"[HPP form] Checkout form posted. Fields: {string.Join(", ", form.AllKeys.OfType<string>())}");
        Console.WriteLine($"Card: {SensitiveData.MaskPan(card.Pan)}  Exp (YYMM): {card.Expiration}  " +
                          $"CVV: {(string.IsNullOrEmpty(card.Cvv) ? "not sent" : "provided")}  Holder: {card.HolderName}  " +
                          $"Email: {billing.EmailAddress ?? "-"}");
        return new PaymentRequest(card, billing);
    }

    /// <summary>
    /// Runs the Sale RedirectData in the checkout page's iframe (3DS challenge, or the Hosted Payment Page form)
    /// and waits for it to reach MerchantResponseUrl.
    /// </summary>
    public async Task<ThreeDSecureCallback> RunInIframeAsync(string redirectData, CancellationToken cancellationToken)
    {
        // The shopper may have left while the Sale was being created; do not load its page over the new one.
        if (_attemptCts.IsCancellationRequested)
            throw new PaymentCancelledException();
        var callbackTcs = _callbackTcs = NewCallbackTcs();
        Console.WriteLine("Loading RedirectData into the checkout iframe (it auto-submits to /Api/spi/Conductor)...");
        await _page!.EvaluateAsync("html => window.showRedirect(html)", redirectData);
        return await WaitForCallbackAsync(callbackTcs, cancellationToken);
    }

    /// <summary>Headless runs: loads the RedirectData as the whole page and waits for MerchantResponseUrl.</summary>
    public async Task<ThreeDSecureCallback> RunAsync(string redirectData, CancellationToken cancellationToken)
    {
        await StartAsync();
        var callbackTcs = _callbackTcs = NewCallbackTcs();

        Console.WriteLine("Loading RedirectData into the browser (its JavaScript will auto-submit to /Api/spi/Conductor)...");
        try
        {
            await _page!.SetContentAsync(redirectData, new PageSetContentOptions { Timeout = 60_000 });
        }
        catch (PlaywrightException ex) when (!_closedTcs.Task.IsCompleted)
        {
            // The HTML auto-submits a form, so the page may navigate before SetContent reports "load".
            // That is expected; the flow is judged by reaching MerchantResponseUrl, not by this call.
            Console.WriteLine($"[warn] SetContent did not complete cleanly (page probably navigated): {ex.Message}");
        }

        return await WaitForCallbackAsync(callbackTcs, cancellationToken);
    }

    private async Task<ThreeDSecureCallback> WaitForCallbackAsync(TaskCompletionSource<ThreeDSecureCallback> callbackTcs,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(options.Browser.ThreeDSecureTimeoutSeconds);
        Console.WriteLine($"Waiting for the flow to reach MerchantResponseUrl (timeout {timeout.TotalSeconds:0}s)...");

        var cancelled = Task.Delay(Timeout.Infinite, _attemptCts.Token);
        var finished = await Task.WhenAny(callbackTcs.Task, _closedTcs.Task, cancelled, Task.Delay(timeout, cancellationToken));
        if (finished == callbackTcs.Task)
            return await callbackTcs.Task;
        if (finished == _closedTcs.Task)
            throw new BrowserClosedException("Browser was closed before MerchantResponseUrl was reached.");
        if (finished == cancelled)
            throw new PaymentCancelledException();

        cancellationToken.ThrowIfCancellationRequested();
        throw new ThreeDSecureException(
            $"Timed out after {timeout.TotalSeconds:0}s without reaching MerchantResponseUrl (the SpiToken lives 5 minutes).");
    }

    /// <summary>Shows the success/error popup on the checkout page.</summary>
    public async Task ShowOutcomeAsync(PaymentOutcome outcome)
    {
        if (!IsOpen) return;
        var json = JsonSerializer.Serialize(new
        {
            kind = outcome.Kind.ToString().ToLowerInvariant(),
            title = outcome.Title,
            message = outcome.Message,
            details = outcome.Details.Select(d => new { label = d.Label, value = d.Value }),
            exception = outcome.Exception,
        });
        try
        {
            await _page!.EvaluateAsync("json => window.showOutcome(JSON.parse(json))", json);
        }
        catch (PlaywrightException ex)
        {
            Console.WriteLine($"[warn] Could not show the result popup: {ex.Message}");
        }
    }

    // Serves new console lines to the checkout page's live console.
    private string PullLogs(int from)
    {
        var lines = recorder.Snapshot();
        from = Math.Clamp(from, 0, lines.Count);
        return JsonSerializer.Serialize(new
        {
            next = lines.Count,
            lines = lines.Skip(from).Select(l => new { t = l.Text, e = l.IsError }),
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
            await _browser.DisposeAsync();
        _playwright?.Dispose();
    }

    private static async Task<IPlaywright> CreatePlaywrightAsync()
    {
        try
        {
            return await Playwright.CreateAsync();
        }
        catch (PlaywrightException ex)
        {
            throw new ThreeDSecureException($"Could not start Playwright: {ex.Message}", ex);
        }
    }

    private async Task<IBrowser> LaunchChromiumAsync(IPlaywright playwright)
    {
        try
        {
            Console.WriteLine($"Opening browser (Chromium, Headless={options.Browser.Headless})...");
            return await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = options.Browser.Headless,
                Args = ["--start-maximized"],
            });
        }
        catch (PlaywrightException ex) when (ex.Message.Contains(ChromiumMissingMarker, StringComparison.OrdinalIgnoreCase))
        {
            throw new ThreeDSecureException(
                "Chromium is not installed for Playwright. Run:  dotnet run -- --install-browsers", ex);
        }
        catch (PlaywrightException ex)
        {
            throw new ThreeDSecureException($"Could not launch Chromium: {ex.Message}", ex);
        }
    }

    private bool IsMerchantResponseUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var candidate)) return false;
        var expected = new Uri(options.MerchantResponseUrl);
        return Uri.Compare(candidate, expected,
                   UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.Unescaped,
                   StringComparison.OrdinalIgnoreCase) == 0;
    }

    private async Task HandleMerchantResponseAsync(IRoute route)
    {
        var callbackTcs = _callbackTcs;
        var request = route.Request;
        Console.WriteLine($"MerchantResponseUrl reached: {request.Method} {SensitiveData.SafeUrl(request.Url)}");
        var callback = ParseCallback(request);

        try
        {
            if (options.Browser.InterceptMerchantResponse)
            {
                // Answered locally, so no merchant server is needed; the checkout page then shows the result popup.
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType = "text/html; charset=utf-8",
                    Body = "<!DOCTYPE html><p style=\"font:15px 'Segoe UI',sans-serif;color:#0b645f\">Authentication complete — completing the payment…</p>",
                });
            }
            else
            {
                await route.ContinueAsync();
            }
        }
        catch (PlaywrightException ex)
        {
            // The callback is still valid; failing to render the page afterwards is not fatal.
            Console.WriteLine($"[warn] Could not complete the MerchantResponseUrl request in the browser: {ex.Message}");
        }

        callbackTcs.TrySetResult(callback);
    }

    private static ThreeDSecureCallback ParseCallback(IRequest request)
    {
        // PowerTranz posts application/x-www-form-urlencoded with a "Response" field holding JSON.
        var raw = request.PostData
                  ?? (Uri.TryCreate(request.Url, UriKind.Absolute, out var u) ? u.Query.TrimStart('?') : "");
        var fields = HttpUtility.ParseQueryString(raw);
        var fieldNames = fields.AllKeys.OfType<string>().ToList();
        var responseJson = fields["Response"];

        if (string.IsNullOrWhiteSpace(responseJson))
            return new ThreeDSecureCallback(request.Url, request.Method, fieldNames, null,
                "No 'Response' field found in the callback.");

        try
        {
            var response = JsonSerializer.Deserialize<SaleResponse>(responseJson, PowerTranzService.JsonOptions);
            return new ThreeDSecureCallback(request.Url, request.Method, fieldNames, response, null);
        }
        catch (JsonException ex)
        {
            return new ThreeDSecureCallback(request.Url, request.Method, fieldNames, null,
                $"'Response' field is not valid JSON: {ex.Message}");
        }
    }

    private static void AttachDiagnostics(IPage page)
    {
        page.FrameNavigated += (_, frame) =>
        {
            if (frame.Url is "about:blank" or "about:srcdoc" or "") return;
            var prefix = frame == page.MainFrame ? "Current URL" : "  [iframe] navigated";
            Console.WriteLine($"{prefix}: {SensitiveData.SafeUrl(frame.Url)}");
        };

        page.Request += (_, request) =>
        {
            if (request.Method == "POST" && request.Url.Contains("/Api/spi/Conductor", StringComparison.OrdinalIgnoreCase))
            {
                // Field names only: values include the SpiToken and browser fingerprint data.
                var names = HttpUtility.ParseQueryString(request.PostData ?? "").AllKeys.OfType<string>();
                Console.WriteLine($"[3DS] RedirectData form submitted to Conductor. Fields: {string.Join(", ", names)}");
            }
        };

        page.Response += (_, response) =>
        {
            if (response.Url.Contains("/Api/spi/", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"[PowerTranz] {response.Request.Method} {SensitiveData.SafeUrl(response.Url)} -> {response.Status}");
        };

        page.RequestFailed += (_, request) =>
            Console.WriteLine($"[request failed] {request.Method} {SensitiveData.SafeUrl(request.Url)}: {request.Failure}");

        page.PageError += (_, error) => Console.WriteLine($"[JavaScript error] {error}");

        page.Console += (_, message) =>
        {
            if (message.Type is "error" or "warning")
                Console.WriteLine($"[browser console {message.Type}] {message.Text}");
        };
    }
}
