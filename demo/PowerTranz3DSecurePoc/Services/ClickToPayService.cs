using System.Net.Http.Json;
using System.Text.Json;
using PowerTranz3DSecurePoc.Models;

namespace PowerTranz3DSecurePoc.Services;

public sealed class ClickToPayException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A Click to Pay checkout turned into PowerTranz Sale inputs.</summary>
public sealed record ClickToPayPayment(CardOptions Card, SaleBillingAddress Billing, Dictionary<string, object> ExtraSourceFields);

/// <summary>
/// Calls MasterCard_ClickToPay_BE: /api/checkout (Mastercard /checkout + decryption) and
/// /api/checkout/confirmations (removed from the API until the new confirmation endpoint exists; the call is best effort
/// and only logs a warning), and maps the decrypted payload to the PowerTranz Sale request.
/// </summary>
public sealed class ClickToPayService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ClickToPayOptions _options;

    public ClickToPayService(ClickToPayOptions options, TimeSpan timeout)
    {
        _options = options;
        var handler = new HttpClientHandler();
        var baseUri = new Uri(options.ApiBaseUrl.TrimEnd('/') + "/");
        if (options.AllowUntrustedLocalhostCertificate && baseUri.IsLoopback)
            handler.ServerCertificateCustomValidationCallback = (message, _, _, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None || message.RequestUri?.IsLoopback == true;
        _httpClient = new HttpClient(handler) { BaseAddress = baseUri, Timeout = timeout };
        _httpClient.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);
    }

    public bool SwaggerHandOff => _options.SwaggerHandOff;

    // This demo runs PowerTranz SPI after /api/checkout, so it has no SpiToken yet: the API only requires one
    // (it does not use it), so a placeholder is sent.
    private const string DemoSpiToken = "poc-no-spi-token-yet";

    private object CheckoutBody(ClickToPayCheckout checkout, decimal amount, string currency) => new
    {
        spiToken = DemoSpiToken,
        srcDpaId = _options.SrcDpaId,
        srcCorrelationId = checkout.CorrelationId,
        merchantTransactionId = checkout.MerchantTransactionId,
        flowId = checkout.FlowId,
        transactionAmount = decimal.Round(amount, 2),
        transactionCurrencyCode = currency,
        xCorrelationId = Guid.NewGuid().ToString(),
    };

    /// <summary>Prints the POST /api/checkout body to copy into Swagger (identifiers only, no card data).</summary>
    public void PrintSwaggerRequest(ClickToPayCheckout checkout, decimal amount, string currency)
    {
        var json = JsonSerializer.Serialize(CheckoutBody(checkout, amount, currency), new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine();
        Console.WriteLine("================ SWAGGER: POST /api/checkout ================");
        Console.WriteLine($"URL:     {_httpClient.BaseAddress}swagger");
        Console.WriteLine("Header:  X-Api-Key (Authorize button) = value of ClickToPay:ApiKey");
        Console.WriteLine("Body:");
        Console.WriteLine(json);
        Console.WriteLine("Single use: Mastercard expires these identifiers after a few minutes.");
        Console.WriteLine("==============================================================");
    }

    public async Task<ClickToPayCompleteResult> CompleteAsync(ClickToPayCheckout checkout, decimal amount, string currency,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"POST {_httpClient.BaseAddress}api/checkout");
        Console.WriteLine($"  Body: srcCorrelationId={checkout.CorrelationId}, merchantTransactionId={SensitiveData.Mask(checkout.MerchantTransactionId)}, " +
                          $"srcDpaId={_options.SrcDpaId}, amount={amount:0.00} {currency}");
        Console.WriteLine("  (the API calls Mastercard POST /srci/api/checkout with OAuth 1.0a and decrypts encryptedPayload)");

        var text = await PostAsync("api/checkout", CheckoutBody(checkout, amount, currency), cancellationToken);
        try
        {
            return JsonSerializer.Deserialize<ClickToPayCompleteResult>(text)
                   ?? throw new ClickToPayException("Click to Pay API returned JSON 'null'.");
        }
        catch (JsonException ex)
        {
            throw new ClickToPayException($"Click to Pay API returned invalid JSON ({text.Length} chars).", ex);
        }
    }

    /// <summary>Best effort: a failed confirmation is logged and never changes the payment outcome.</summary>
    public async Task ConfirmAsync(ClickToPayCheckout checkout, decimal amount, string currency, bool approved,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine($"POST {_httpClient.BaseAddress}api/checkout/confirmations (approved={approved})");
        try
        {
            // Codes as in Mastercard's sample: "01" for the event; confirmationStatus "01" success, "02" failure.
            var status = approved ? "01" : "02";
            await PostAsync("api/checkout/confirmations", new
            {
                correlationId = checkout.CorrelationId,
                merchantTransactionId = checkout.MerchantTransactionId,
                flowId = checkout.FlowId,
                xCorrelationId = Guid.NewGuid().ToString(),
                confirmationData = new
                {
                    checkoutEventType = "01",
                    checkoutEventStatus = status,
                    confirmationStatus = status,
                    confirmationReason = approved ? "The payment was successful" : "The payment was declined",
                    transactionAmount = new { transactionAmount = decimal.Round(amount, 2), transactionCurrencyCode = currency },
                },
            }, cancellationToken);
            Console.WriteLine("Click to Pay confirmation sent to Mastercard.");
        }
        catch (ClickToPayException ex)
        {
            Console.WriteLine($"[warn] Click to Pay confirmation failed: {ex.Message}");
        }
    }

    private async Task<string> PostAsync(string path, object body, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsJsonAsync(path, body, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new ClickToPayException($"Could not reach the Click to Pay API at {_httpClient.BaseAddress}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ClickToPayException($"Click to Pay API timed out after {_httpClient.Timeout.TotalSeconds}s.", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            Console.WriteLine($"Click to Pay API HTTP status: {(int)response.StatusCode} {response.StatusCode}");
            if (!response.IsSuccessStatusCode)
                throw new ClickToPayException($"Click to Pay API /{path} failed with HTTP {(int)response.StatusCode}. {DescribeProblem(text)}");
            return text;
        }
    }

    // Problem Details title/detail only; the API never returns payload data in errors.
    private static string DescribeProblem(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
            var detail = root.TryGetProperty("detail", out var d) ? d.GetString() : null;
            if (title is not null || detail is not null) return $"{title} {detail}".Trim();
        }
        catch (JsonException)
        {
            // Not JSON.
        }
        return $"Body ({body.Length} chars) not shown.";
    }

    /// <summary>
    /// /api/checkout flat response → PowerTranz Sale inputs, as the real UI does: CardNo/CardExpDate (YYMM) go in
    /// CardPan/CardExpiration, CardCVV2 (dynamic security code) in CardCvv, and the non-null BillTo* fields in
    /// BillingAddress. The response has no cryptogram or ECI.
    /// </summary>
    public ClickToPayPayment Map(ClickToPayCompleteResult result)
    {
        if (string.IsNullOrWhiteSpace(result.CardNo) || string.IsNullOrWhiteSpace(result.CardExpDate))
            throw new ClickToPayException("Click to Pay API returned no cardNo / cardExpDate.");

        var name = result.CardholderName ?? $"{result.BillToFirstName} {result.BillToLastName}".Trim();
        var card = new CardOptions
        {
            Pan = result.CardNo,
            Expiration = result.CardExpDate,
            Cvv = result.CardCvv2 ?? "",
            HolderName = name,
        };
        var billing = new SaleBillingAddress
        {
            FirstName = result.BillToFirstName,
            LastName = result.BillToLastName,
            Line1 = result.BillToAddress1,
            Line2 = result.BillToAddress2,
            State = result.BillToState,
            PostalCode = result.BillToPostCode,
            CountryCode = CountryCodes.ToNumeric(result.BillToCountry),
            EmailAddress = result.BillToEmail,
            PhoneNumber = result.BillToTelephone,
        };

        Console.WriteLine("Click to Pay payload decrypted:");
        Console.WriteLine($"  Card number:    {SensitiveData.MaskPan(result.CardNo)}  Exp (YYMM): {result.CardExpDate}");
        Console.WriteLine($"  CVV2 (DTVC):    {(result.CardCvv2 is null ? "-" : "*** (sent as CardCvv)")}");
        Console.WriteLine($"  Cardholder:     {(string.IsNullOrEmpty(name) ? "-" : name)}  Email: {result.BillToEmail ?? "-"}");
        Console.WriteLine($"  Bill to:        {result.BillToState} {result.BillToPostCode} {result.BillToCountry}");
        Console.WriteLine($"  Ship to:        {result.ShipToState} {result.ShipToPostCode} {result.ShipToCountry}");

        return new ClickToPayPayment(card, billing, []);
    }

    public void Dispose() => _httpClient.Dispose();
}

/// <summary>Click to Pay sends ISO alpha-2 country codes; the PowerTranz HPP template sends ISO numeric ones.</summary>
internal static class CountryCodes
{
    private static readonly Dictionary<string, string> Numeric = new(StringComparer.OrdinalIgnoreCase)
    {
        ["US"] = "840", ["CA"] = "124", ["MX"] = "484", ["GB"] = "826", ["ES"] = "724", ["FR"] = "250",
        ["DE"] = "276", ["IT"] = "380", ["NL"] = "528", ["IE"] = "372", ["PT"] = "620", ["BR"] = "076",
        ["AR"] = "032", ["CO"] = "170", ["CL"] = "152", ["PE"] = "604", ["JM"] = "388", ["TT"] = "780",
        ["BB"] = "052", ["BS"] = "044", ["KY"] = "136", ["DO"] = "214", ["PR"] = "630", ["PA"] = "591",
        ["CR"] = "188", ["GT"] = "320", ["AE"] = "784", ["PH"] = "608", ["VN"] = "704", ["KW"] = "414",
        ["AU"] = "036", ["IN"] = "356",
    };

    public static string? ToNumeric(string? alpha2) =>
        string.IsNullOrWhiteSpace(alpha2) ? null : Numeric.GetValueOrDefault(alpha2, alpha2);
}
