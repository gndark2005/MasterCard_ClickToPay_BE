using Microsoft.Extensions.Configuration;
using PowerTranz3DSecurePoc;
using PowerTranz3DSecurePoc.Demo;
using PowerTranz3DSecurePoc.Models;
using PowerTranz3DSecurePoc.Services;
using PowerTranz3DSecurePoc.Shop;

// One-off helper: downloads the Chromium build that matches the installed Microsoft.Playwright version.
if (args.Contains("--install-browsers"))
    return Microsoft.Playwright.Program.Main(["install", "chromium"]);

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();

var options = configuration.GetSection(PowerTranzOptions.SectionName).Get<PowerTranzOptions>() ?? new PowerTranzOptions();
var shop = configuration.GetSection(ShopOptions.SectionName).Get<ShopOptions>() ?? new ShopOptions();
var clickToPay = configuration.GetSection(ClickToPayOptions.SectionName).Get<ClickToPayOptions>() ?? new ClickToPayOptions();
var missing = options.GetMissingSettings().Concat(clickToPay.GetMissingSettings()).ToList();
if (!options.Browser.Headless && shop.Products.Count == 0)
    missing.Add("Shop:Products (at least one product for the cart page)");
if (missing.Count > 0)
{
    Console.Error.WriteLine("Missing configuration (use User Secrets or environment variables for secrets):");
    missing.ForEach(m => Console.Error.WriteLine($"  - {m}"));
    return 1;
}

// Click to Pay amounts use ISO alphabetic currency codes; PowerTranz uses numeric ones.
string? clickToPayCurrency = null;
if (clickToPay.Enabled)
{
    try
    {
        clickToPayCurrency = clickToPay.ResolveCurrency(options.CurrencyCode);
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("Cancellation requested...");
    cts.Cancel();
};

using var httpClient = new HttpClient
{
    BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"),
    Timeout = TimeSpan.FromSeconds(options.HttpTimeoutSeconds),
};

// Everything printed from here on is streamed into the checkout page's API console.
var recorder = new ConsoleRecorder();
recorder.Install();

await using var browser = new ThreeDSecureBrowserService(options, recorder, clickToPay);
var powerTranz = new PowerTranzService(httpClient, options);
using var clickToPayService = clickToPay.Enabled
    ? new ClickToPayService(clickToPay, TimeSpan.FromSeconds(options.HttpTimeoutSeconds))
    : null;

try
{
    if (options.Browser.Headless)
    {
        // Headless runs skip the shop UI and pay once with the configured amount and card.
        var outcome = await RunPaymentAsync(powerTranz, browser.RunAsync, options.TotalAmount, options.Card, null, cts.Token);
        return outcome.Kind switch { OutcomeKind.Approved => 0, OutcomeKind.Declined => 4, _ => 2 };
    }

    await RunDemoAsync(powerTranz, browser, options, shop, clickToPayService, clickToPayCurrency, cts.Token);
    return 0;
}
catch (BrowserClosedException)
{
    Console.WriteLine("Browser closed. Bye.");
    return 0;
}
catch (ThreeDSecureException ex)
{
    Console.Error.WriteLine($"Browser error: {ex.Message}");
    return 3;
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    Console.Error.WriteLine("Cancelled by user.");
    return 130;
}

// Cart → checkout page; the checkout page stays open so the shopper can retry with other cards until
// they go back to the cart or close the browser.
static async Task RunDemoAsync(PowerTranzService powerTranz, ThreeDSecureBrowserService browser,
    PowerTranzOptions options, ShopOptions shop, ClickToPayService? clickToPay, string? clickToPayCurrency,
    CancellationToken cancellationToken)
{
    Order? order = null;
    while (true)
    {
        order = await browser.PromptCheckoutAsync(shop, order, cancellationToken);
        await browser.ShowCheckoutAsync(order, shop, options.CurrencyCode, options.HostedPage.IsConfigured,
            clickToPay is null ? null : clickToPayCurrency);

        while (await browser.WaitForPaymentAsync(cancellationToken) is { } request)
        {
            var outcome = request.ClickToPay is { } checkout && clickToPay is not null
                ? await RunClickToPayPaymentAsync(powerTranz, clickToPay, browser, checkout, order.Total,
                    clickToPayCurrency!, cancellationToken)
                : await RunPaymentAsync(powerTranz, browser.RunInIframeAsync, order.Total, request.Card,
                    request.Billing, cancellationToken);
            // A cancelled attempt already moved on (back to cart or another payment method): no popup.
            if (outcome.Kind != OutcomeKind.Cancelled)
                await browser.ShowOutcomeAsync(outcome);
        }
    }
}

// Click to Pay: Mastercard /checkout + decryption (MasterCard_ClickToPay_BE) → the usual PowerTranz flow with the
// network token → /checkout/confirmations with the result.
static async Task<PaymentOutcome> RunClickToPayPaymentAsync(PowerTranzService powerTranz, ClickToPayService clickToPay,
    ThreeDSecureBrowserService browser, ClickToPayCheckout checkout, decimal amount, string currency,
    CancellationToken cancellationToken)
{
    ClickToPayPayment payment;
    try
    {
        Console.WriteLine();
        Console.WriteLine("STEP 0/3 — Click to Pay: Mastercard /checkout and payload decryption (MasterCard_ClickToPay_BE)");
        var result = await clickToPay.CompleteAsync(checkout, amount, currency, cancellationToken);
        payment = clickToPay.Map(result);
    }
    catch (ClickToPayException ex)
    {
        Console.Error.WriteLine($"Click to Pay error: {ex.Message}");
        return new PaymentOutcome(OutcomeKind.Error, "Click to Pay error", ex.Message, []);
    }

    var outcome = await RunPaymentAsync(powerTranz, browser.RunInIframeAsync, amount, payment.Card, payment.Billing,
        cancellationToken, payment.ExtraSourceFields);
    if (outcome.Kind != OutcomeKind.Cancelled)
        await clickToPay.ConfirmAsync(checkout, amount, currency, outcome.Kind == OutcomeKind.Approved, cancellationToken);
    return outcome;
}

// One payment attempt: Auth/Sale → RedirectData (3DS, or the Hosted Payment Page when card is null) → Payment.
// billing comes from the checkout form; null keeps the configured billing address.
// Every failure becomes an outcome for the popup; only a closed browser or Ctrl+C ends the demo.
static async Task<PaymentOutcome> RunPaymentAsync(PowerTranzService powerTranz,
    Func<string, CancellationToken, Task<ThreeDSecureCallback>> runRedirect,
    decimal amount, CardOptions? card, SaleBillingAddress? billing, CancellationToken cancellationToken,
    Dictionary<string, object>? extraSourceFields = null)
{
    try
    {
        var name = powerTranz.TransactionName;
        Console.WriteLine();
        Console.WriteLine(card is null
            ? $"STEP 1/3 — {name} with Hosted Payment Page (no card data yet, no money is charged)"
            : $"STEP 1/3 — {name} (3DS preprocessing, no money is charged yet)");
        var sale = card is null
            ? await powerTranz.HostedPageSaleAsync(amount, cancellationToken)
            : await powerTranz.SaleAsync(card, amount, billing, cancellationToken, extraSourceFields);
        Console.WriteLine($"{name} response received.");
        PrintResponse(sale);

        if (sale.Errors is { Count: > 0 })
        {
            PrintErrors(sale.Errors);
            return Result(OutcomeKind.Error, $"{name} rejected by PowerTranz", Describe(sale.Errors), sale);
        }

        if (!sale.RequiresThreeDSecure)
        {
            Console.WriteLine(sale.Approved ? "RESULT: PAYMENT APPROVED (no 3DS redirect)" : "RESULT: SALE DECLINED");
            return sale.Approved
                ? Result(OutcomeKind.Approved, "Payment approved", "Approved without a 3-D Secure redirect.", sale)
                : Result(OutcomeKind.Declined, "Payment declined", $"[{sale.IsoResponseCode}] {sale.ResponseMessage}", sale);
        }

        if (string.IsNullOrWhiteSpace(sale.RedirectData))
        {
            Console.Error.WriteLine("IsoResponseCode SP4 (SPI preprocessing) but RedirectData is empty; cannot continue.");
            return Result(OutcomeKind.Error, $"{name} response incomplete", "SP4 was returned without RedirectData.", sale);
        }
        Console.WriteLine("SPI preprocessing complete (SP4 is expected here, not a decline).");

        Console.WriteLine();
        Console.WriteLine(card is null
            ? "STEP 2/3 — Card entry and 3DS in the PowerTranz Hosted Payment Page (does NOT charge)"
            : "STEP 2/3 — 3DS authentication (verifies the cardholder, does NOT charge)");
        var callback = await runRedirect(sale.RedirectData, cancellationToken);

        Console.WriteLine("3DS authentication result received.");
        Console.WriteLine($"MerchantResponseUrl: {callback.Method} {SensitiveData.SafeUrl(callback.Url)}");
        Console.WriteLine($"Callback fields: {string.Join(", ", callback.FieldNames)}");
        if (callback.Response is not { } authentication)
        {
            Console.Error.WriteLine($"Could not read the 3DS result: {callback.ParseError}");
            return Result(OutcomeKind.Error, "Could not read the 3-D Secure result", callback.ParseError ?? "", sale);
        }
        PrintResponse(authentication);
        // The callback can carry errors too (e.g. 3D3 "3DS2 error" with [543] "3DS2 notify error").
        if (authentication.Errors is { Count: > 0 })
            PrintErrors(authentication.Errors);
        Console.WriteLine($"  => {Describe3DSResult(authentication)}");
        // Shown in the popup too: PowerTranz requires displaying the issuer message to the cardholder.
        var issuerMessage = authentication.RiskManagement?.ThreeDSecure?.CardholderInfo;
        if (!string.IsNullOrWhiteSpace(issuerMessage))
            Console.WriteLine($"  => Message from your card issuer: {issuerMessage}");
        Console.WriteLine("  (Approved=False is normal at this step: authentication never charges the card.)");

        // The callback normally carries the SpiToken; fall back to the one from Sale if it does not.
        var spiToken = authentication.SpiToken ?? sale.SpiToken;
        if (string.IsNullOrWhiteSpace(spiToken))
        {
            Console.Error.WriteLine("No SpiToken in the 3DS callback or the Sale response; cannot call /Api/spi/Payment.");
            return Result(OutcomeKind.Error, "Missing SpiToken", "No SpiToken to complete the payment.", authentication);
        }

        Console.WriteLine();
        Console.WriteLine("STEP 3/3 — Payment (authorizes and charges the card)");
        var payment = await powerTranz.PaymentAsync(spiToken, cancellationToken);
        Console.WriteLine("Payment response received.");
        PrintResponse(payment);
        if (payment.Errors is { Count: > 0 })
            PrintErrors(payment.Errors);

        Console.WriteLine(payment.Approved ? "RESULT: PAYMENT APPROVED" : "RESULT: PAYMENT NOT APPROVED");
        var details = Details(payment, authentication, issuerMessage);
        if (payment.Approved)
            return new PaymentOutcome(OutcomeKind.Approved, "Payment approved",
                $"{payment.TotalAmount:0.00} charged. {payment.ResponseMessage}", details);

        var reason = $"[{payment.IsoResponseCode}] {payment.ResponseMessage}";
        if (payment.Errors is { Count: > 0 })
            reason += " " + Describe(payment.Errors);
        return new PaymentOutcome(OutcomeKind.Declined, "Payment not approved", reason, details);
    }
    catch (PaymentCancelledException ex)
    {
        Console.WriteLine($"RESULT: CANCELLED — {ex.Message} (the PowerTranz transaction is left unpaid)");
        return new PaymentOutcome(OutcomeKind.Cancelled, "Payment cancelled", ex.Message, []);
    }
    catch (PowerTranzException ex)
    {
        Console.Error.WriteLine($"PowerTranz error: {ex.Message}");
        return Failure("PowerTranz error", ex, full: false);
    }
    catch (ThreeDSecureException ex) when (ex is not BrowserClosedException)
    {
        Console.Error.WriteLine($"3DS error: {ex.Message}");
        return Failure("3-D Secure error", ex, full: false);
    }
    catch (Exception ex) when (ex is not (BrowserClosedException or OperationCanceledException))
    {
        Console.Error.WriteLine($"Unexpected error: {ex}");
        return Failure("Unexpected error", ex, full: true);
    }
}

static PaymentOutcome Result(OutcomeKind kind, string title, string message, SaleResponse response) =>
    new(kind, title, message, Details(response, null, null));

static PaymentOutcome Failure(string title, Exception ex, bool full) =>
    new(OutcomeKind.Error, title, ex.Message, [], full ? ex.ToString() : $"{ex.GetType().FullName}: {ex.Message}");

// Popup details; values are the same (already safe) ones printed to the console.
static List<OutcomeDetail> Details(SaleResponse response, SaleResponse? authentication, string? issuerMessage)
{
    var details = new List<OutcomeDetail>();
    void Add(string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) details.Add(new OutcomeDetail(label, value));
    }

    Add("Amount", $"{response.TotalAmount:0.00} ({response.CurrencyCode})");
    Add("ISO response", $"{response.IsoResponseCode} — {response.ResponseMessage}");
    Add("Authorization code", response.AuthorizationCode);
    Add("RRN", response.RRN);
    Add("Card brand", response.CardBrand ?? authentication?.CardBrand);
    if (authentication is not null)
        Add("3-D Secure", $"{authentication.IsoResponseCode} — {authentication.ResponseMessage}");
    Add("Issuer message", issuerMessage);
    Add("Transaction ID", response.TransactionIdentifier);
    Add("Order ID", response.OrderIdentifier);
    return details;
}

static string Describe(List<PowerTranzError> errors) => string.Join(" ", errors.Select(e => $"[{e.Code}] {e.Message}"));

static void PrintErrors(List<PowerTranzError> errors) =>
    errors.ForEach(e => Console.Error.WriteLine($"  [{e.Code}] {e.Message}"));

// 3D0 = authenticated; SP1/3D1 = card does not support 3DS; others = authentication failed (no liability shift).
static string Describe3DSResult(SaleResponse r) => r.IsoResponseCode switch
{
    "3D0" => "Cardholder AUTHENTICATED with 3DS.",
    "3D1" => "3DS NOT performed: card/merchant not enrolled. Payment will be attempted WITHOUT 3DS authentication.",
    "SP1" => "3DS NOT supported for this card. Payment will be attempted WITHOUT 3DS authentication.",
    "3D3" => $"3DS ERROR ({r.ResponseMessage}). Payment will be attempted anyway; PowerTranz is expected to decline it.",
    _ => $"3DS NOT authenticated ({r.IsoResponseCode}: {r.ResponseMessage}). Payment will be attempted anyway.",
};

static void PrintResponse(SaleResponse r)
{
    Console.WriteLine($"  TransactionIdentifier: {r.TransactionIdentifier}");
    Console.WriteLine($"  OrderIdentifier:       {r.OrderIdentifier}");
    Console.WriteLine($"  TransactionType:       {r.TransactionType}");
    Console.WriteLine($"  Approved:              {r.Approved}");
    Console.WriteLine($"  IsoResponseCode:       {r.IsoResponseCode}");
    Console.WriteLine($"  ResponseMessage:       {r.ResponseMessage}");
    Console.WriteLine($"  TotalAmount:           {r.TotalAmount} ({r.CurrencyCode})");
    if (r.CardBrand is not null)
        Console.WriteLine($"  CardBrand:             {r.CardBrand}");
    Console.WriteLine($"  SpiToken:              {SensitiveData.Mask(r.SpiToken)}");
    if (r.AuthorizationCode is not null)
        Console.WriteLine($"  AuthorizationCode:     {r.AuthorizationCode}");
    if (r.RRN is not null)
        Console.WriteLine($"  RRN:                   {r.RRN}");
    if (r.RedirectData is not null)
        Console.WriteLine($"  RedirectData:          {r.RedirectData.Length} chars of HTML");
    if (r.RiskManagement?.ThreeDSecure is { } tds &&
        (tds.Eci ?? tds.AuthenticationStatus ?? tds.ProtocolVersion) is not null)
    {
        Console.WriteLine($"  3DS Eci:               {tds.Eci}");
        Console.WriteLine($"  3DS AuthStatus:        {tds.AuthenticationStatus}");
        Console.WriteLine($"  3DS ProtocolVersion:   {tds.ProtocolVersion}");
    }
}
