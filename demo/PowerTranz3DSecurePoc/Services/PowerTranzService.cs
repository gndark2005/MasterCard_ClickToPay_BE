using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PowerTranz3DSecurePoc.Models;

namespace PowerTranz3DSecurePoc.Services;

public sealed class PowerTranzException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class PowerTranzService(HttpClient httpClient, PowerTranzOptions options)
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>First SPI request name ("Auth" or "Sale"), for logs.</summary>
    public string TransactionName => options.SpiTransaction;

    /// <summary>
    /// Auth/Sale with the card in the request; RedirectData only runs the 3DS flow.
    /// <paramref name="billing"/> (e.g. from the checkout form) replaces the configured billing address.
    /// </summary>
    public Task<SaleResponse> SaleAsync(CardOptions card, decimal amount, SaleBillingAddress? billing,
        CancellationToken cancellationToken, Dictionary<string, object>? extraSourceFields = null)
    {
        var request = BuildSaleRequest(card, amount);
        if (billing is not null)
            request.BillingAddress = billing;
        if (extraSourceFields is { Count: > 0 })
            request.Source.ExtraFields = extraSourceFields;
        return SendSaleAsync(request, cancellationToken);
    }

    /// <summary>
    /// Auth/Sale without card data: RedirectData loads the configured Hosted Payment Page, where the cardholder
    /// enters the card before 3DS runs.
    /// </summary>
    public Task<SaleResponse> HostedPageSaleAsync(decimal amount, CancellationToken cancellationToken)
    {
        var request = BuildSaleRequest(card: null, amount);
        request.Source = new SaleSource
        {
            CardPresent = false,
            CardEmvFallback = false,
            ManualEntry = false,
            Debit = false,
            Contactless = false,
        };
        request.ExtendedData.HostedPage = new SaleHostedPage
        {
            PageSet = options.HostedPage.PageSet,
            PageName = options.HostedPage.PageName,
        };
        Console.WriteLine($"Hosted Payment Page: {options.HostedPage.PageSet} / {options.HostedPage.PageName}");
        return SendSaleAsync(request, cancellationToken);
    }

    private async Task<SaleResponse> SendSaleAsync(SaleRequest request, CancellationToken cancellationToken)
    {
        var path = $"Api/spi/{options.SpiTransaction}";
        var gatewayKey = string.IsNullOrWhiteSpace(options.GatewayKey) ? "" : $", PowerTranz-GatewayKey={options.GatewayKey}";
        Console.WriteLine($"POST {httpClient.BaseAddress}{path}");
        Console.WriteLine($"  Headers: PowerTranz-PowerTranzId={SensitiveData.Mask(options.PowerTranzId)}, PowerTranz-PowerTranzPassword=***{gatewayKey}");
        Console.WriteLine($"  Body: {MaskedJson(request)}");

        using var message = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(request, options: JsonOptions),
        };
        message.Headers.Add("PowerTranz-PowerTranzId", options.PowerTranzId);
        message.Headers.Add("PowerTranz-PowerTranzPassword", options.PowerTranzPassword);
        if (!string.IsNullOrWhiteSpace(options.GatewayKey))
            message.Headers.Add("PowerTranz-GatewayKey", options.GatewayKey);
        message.Headers.Accept.ParseAdd("application/json");

        return await SendAsync(message, options.SpiTransaction, cancellationToken);
    }

    /// <summary>
    /// Completes an SPI transaction after 3DS. The body is the SpiToken as a JSON string
    /// and no PowerTranz credential headers are sent (the token identifies the transaction).
    /// </summary>
    public async Task<SaleResponse> PaymentAsync(string spiToken, CancellationToken cancellationToken)
    {
        Console.WriteLine($"POST {httpClient.BaseAddress}Api/spi/Payment");
        Console.WriteLine($"  Body: \"{SensitiveData.Mask(spiToken)}\" (the SpiToken; no credential headers)");

        using var message = new HttpRequestMessage(HttpMethod.Post, "Api/spi/Payment")
        {
            Content = JsonContent.Create(spiToken, options: JsonOptions),
        };
        message.Headers.Accept.ParseAdd("application/json");

        return await SendAsync(message, "Payment", cancellationToken);
    }

    private async Task<SaleResponse> SendAsync(HttpRequestMessage message, string operation, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(message, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new PowerTranzException($"Could not reach PowerTranz at {httpClient.BaseAddress}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PowerTranzException($"{operation} request timed out after {httpClient.Timeout.TotalSeconds}s.", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            Console.WriteLine($"{operation} HTTP status: {(int)response.StatusCode} {response.StatusCode}");

            if (!response.IsSuccessStatusCode)
                throw new PowerTranzException(
                    $"{operation} failed with HTTP {(int)response.StatusCode}. {DescribeErrorBody(body)}");

            if (string.IsNullOrWhiteSpace(body))
                throw new PowerTranzException($"{operation} returned an empty body.");

            SaleResponse? result;
            try
            {
                result = JsonSerializer.Deserialize<SaleResponse>(body, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new PowerTranzException($"{operation} returned invalid JSON ({body.Length} chars): {ex.Message}", ex);
            }

            return result ?? throw new PowerTranzException($"{operation} returned JSON 'null'.");
        }
    }

    private SaleRequest BuildSaleRequest(CardOptions? card, decimal amount)
    {
        var billing = options.BillingAddress;
        return new SaleRequest
        {
            TransactionIdentifier = Guid.NewGuid().ToString(),
            TotalAmount = decimal.Round(amount, 2),
            CurrencyCode = options.CurrencyCode,
            ThreeDSecure = true,
            OrderIdentifier = $"POC-3DS-{DateTime.UtcNow:yyyyMMddHHmmss}",
            AddressMatch = false,
            Source = card is null ? new SaleSource() : new SaleSource
            {
                CardPan = card.Pan,
                CardCvv = string.IsNullOrWhiteSpace(card.Cvv) ? null : card.Cvv,
                CardExpiration = card.Expiration,
                CardholderName = card.HolderName,
            },
            BillingAddress = billing is null ? null : new SaleBillingAddress
            {
                FirstName = billing.FirstName,
                LastName = billing.LastName,
                Line1 = billing.Line1,
                City = billing.City,
                State = billing.State,
                PostalCode = billing.PostalCode,
                CountryCode = billing.CountryCode,
                EmailAddress = billing.EmailAddress,
                PhoneNumber = billing.PhoneNumber,
            },
            ExtendedData = new SaleExtendedData
            {
                ThreeDSecure = new SaleThreeDSecure { ChallengeIndicator = options.ChallengeIndicator },
                MerchantResponseUrl = options.MerchantResponseUrl,
            },
        };
    }

    // Request as sent, with the card number masked and the CVV hidden, for the demo console.
    private static string MaskedJson(SaleRequest request)
    {
        var node = JsonSerializer.SerializeToNode(request, JsonOptions)!;
        if (node["Source"] is JsonObject source)
        {
            if (source["CardPan"] is not null) source["CardPan"] = SensitiveData.MaskPan(request.Source.CardPan);
            if (source["CardCvv"] is not null) source["CardCvv"] = "***";
            foreach (var key in request.Source.ExtraFields?.Keys ?? Enumerable.Empty<string>())
                source[key] = SensitiveData.Mask(request.Source.ExtraFields![key]?.ToString());
        }
        // Display-only JSON (the console inserts it as text), so the masking dots need not be \u-escaped.
        return node.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    // Error bodies may echo request data, so only PowerTranz error codes/messages are shown.
    private static string DescribeErrorBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "Empty body.";
        try
        {
            var parsed = JsonSerializer.Deserialize<SaleResponse>(body, JsonOptions);
            if (parsed?.Errors is { Count: > 0 } errors)
                return string.Join("; ", errors.Select(e => $"[{e.Code}] {e.Message}"));
            if (parsed?.ResponseMessage is { } msg)
                return $"[{parsed.IsoResponseCode}] {msg}";
        }
        catch (JsonException)
        {
            // Not JSON (e.g. an HTML error page); fall through and report its size only.
        }
        return $"Body ({body.Length} chars) not shown.";
    }
}
