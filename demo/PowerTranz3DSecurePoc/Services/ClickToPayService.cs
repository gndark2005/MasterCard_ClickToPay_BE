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

    private object CheckoutBody(ClickToPayCheckout checkout) => new
    {
        spiToken = DemoSpiToken,
        srcDpaId = _options.SrcDpaId,
        srcCorrelationId = checkout.CorrelationId,
        merchantTransactionId = checkout.MerchantTransactionId,
        flowId = checkout.FlowId,
        xCorrelationId = Guid.NewGuid().ToString(),
    };

    /// <summary>Prints the POST /api/checkout body to copy into Swagger (identifiers only, no card data).</summary>
    public void PrintSwaggerRequest(ClickToPayCheckout checkout)
    {
        var json = JsonSerializer.Serialize(CheckoutBody(checkout), new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine();
        Console.WriteLine("================ SWAGGER: POST /api/checkout ================");
        Console.WriteLine($"URL:     {_httpClient.BaseAddress}swagger");
        Console.WriteLine("Header:  X-Api-Key (Authorize button) = value of ClickToPay:ApiKey");
        Console.WriteLine("Body:");
        Console.WriteLine(json);
        Console.WriteLine("Single use: Mastercard expires these identifiers after a few minutes.");
        Console.WriteLine("==============================================================");
    }

    public async Task<ClickToPayCompleteResult> CompleteAsync(ClickToPayCheckout checkout, CancellationToken cancellationToken)
    {
        Console.WriteLine($"POST {_httpClient.BaseAddress}api/checkout");
        Console.WriteLine($"  Body: srcCorrelationId={checkout.CorrelationId}, merchantTransactionId={SensitiveData.Mask(checkout.MerchantTransactionId)}, " +
                          $"srcDpaId={_options.SrcDpaId}");
        Console.WriteLine("  (the API calls Mastercard POST /srci/api/checkout with OAuth 1.0a and decrypts encryptedPayload)");

        var text = await PostAsync("api/checkout", CheckoutBody(checkout), cancellationToken);
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
            await PostAsync("api/checkout/confirmations", new
            {
                correlationId = checkout.CorrelationId,
                merchantTransactionId = checkout.MerchantTransactionId,
                flowId = checkout.FlowId,
                transactionAmount = decimal.Round(amount, 2),
                transactionCurrencyCode = currency,
                approved,
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
    /// Decrypted payload → PowerTranz Sale inputs. The /api/checkout response always has payload.card (the PAN, or
    /// the network token when Mastercard sent only a token): it goes in CardPan with its expiry (YYMM). A dynamic
    /// security code (DYNAMIC_CARD_SECURITY_CODE) goes in CardCvv. A DSRP cryptogram and the ECI are only added to
    /// Source under the field names configured in ClickToPay:CryptogramSourceField / EciSourceField (pending
    /// confirmation in the PowerTranz SPI docs).
    /// </summary>
    public ClickToPayPayment Map(ClickToPayCompleteResult result)
    {
        var payload = result.Payload ?? throw new ClickToPayException("Click to Pay API returned no payload.");
        var token = payload.Token;
        var accountNumber = payload.Card?.PrimaryAccountNumber ?? token?.PaymentToken;
        if (string.IsNullOrWhiteSpace(accountNumber))
            throw new ClickToPayException("The decrypted payload has no card number (payload.card) nor payment token.");

        var fromCard = !string.IsNullOrWhiteSpace(payload.Card?.PrimaryAccountNumber);
        var month = ((fromCard ? payload.Card!.PanExpirationMonth : token?.TokenExpirationMonth) ?? "").PadLeft(2, '0');
        var year = (fromCard ? payload.Card!.PanExpirationYear : token?.TokenExpirationYear) ?? "";
        var expiration = (year.Length >= 2 ? year[^2..] : year) + month;

        var name = payload.Card?.CardholderFullName ?? token?.CardholderFullName ?? payload.ConsumerFullName
                   ?? $"{payload.ConsumerFirstName} {payload.ConsumerLastName}".Trim();
        var (firstName, lastName) = SplitName(payload.ConsumerFirstName, payload.ConsumerLastName, name);

        var dynamicType = payload.DynamicData?.DynamicDataType;
        var dynamicValue = payload.DynamicData?.DynamicDataValue;
        var securityCode = dynamicType == "DYNAMIC_CARD_SECURITY_CODE" ? dynamicValue ?? "" : "";
        var card = new CardOptions { Pan = accountNumber, Expiration = expiration, Cvv = securityCode, HolderName = name };

        var address = payload.BillingAddress ?? payload.ShippingAddress;
        var phone = payload.ConsumerMobileNumber;
        var billing = new SaleBillingAddress
        {
            FirstName = firstName,
            LastName = lastName,
            Line1 = address?.Line1,
            Line2 = address?.Line2,
            City = address?.City,
            State = address?.State,
            PostalCode = address?.Zip,
            CountryCode = CountryCodes.ToNumeric(address?.CountryCode),
            EmailAddress = payload.ConsumerEmailAddress,
            PhoneNumber = phone?.PhoneNumber is { } number ? $"{phone.CountryCode}{number}" : null,
        };

        var cryptogram = dynamicType == "CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM" ? dynamicValue : null;
        var extra = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(_options.CryptogramSourceField) && !string.IsNullOrWhiteSpace(cryptogram))
            extra[_options.CryptogramSourceField] = cryptogram;
        if (!string.IsNullOrWhiteSpace(_options.EciSourceField) && !string.IsNullOrWhiteSpace(result.Eci))
            extra[_options.EciSourceField] = result.Eci;

        Console.WriteLine("Click to Pay payload decrypted:");
        Console.WriteLine($"  Format:         credentialType={result.CredentialType ?? "-"}, dynamicDataType={dynamicType ?? "-"}");
        Console.WriteLine($"  Card number:    {SensitiveData.MaskPan(accountNumber)}  Exp (YYMM): {expiration}" +
                          (fromCard ? "  (payload.card)" : "  (payload.token)"));
        Console.WriteLine($"  PAR:            {SensitiveData.Mask(payload.Card?.PaymentAccountReference ?? token?.PaymentAccountReference)}");
        Console.WriteLine($"  Cryptogram:     {SensitiveData.Mask(cryptogram)}");
        Console.WriteLine($"  Dynamic CVC:    {(string.IsNullOrEmpty(securityCode) ? "-" : "*** (sent as CardCvv)")}");
        Console.WriteLine($"  ECI:            {result.Eci ?? "-"}");
        Console.WriteLine($"  Cardholder:     {name}  Email: {payload.ConsumerEmailAddress ?? "-"}");
        Console.WriteLine($"  Billing:        {address?.City}, {address?.State} {address?.Zip} {address?.CountryCode}");
        Console.WriteLine(extra.Count == 0
            ? "  [warn] Cryptogram/ECI NOT sent to PowerTranz: set ClickToPay:CryptogramSourceField and EciSourceField " +
              "from the PowerTranz SPI docs. The card number is sent as CardPan."
            : $"  => Sent in Source as: {string.Join(", ", extra.Keys)}");

        return new ClickToPayPayment(card, billing, extra);
    }

    private static (string? First, string? Last) SplitName(string? first, string? last, string full)
    {
        if (!string.IsNullOrWhiteSpace(first) || !string.IsNullOrWhiteSpace(last)) return (first, last);
        var space = full.LastIndexOf(' ');
        return space < 0 ? (full, null) : (full[..space], full[(space + 1)..]);
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
