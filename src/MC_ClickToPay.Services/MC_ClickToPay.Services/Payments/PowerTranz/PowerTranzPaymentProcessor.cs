using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MC_ClickToPay.Services.Payments.PowerTranz;

/// <summary>
/// Charges the decrypted Click to Pay credential with a server-to-server PowerTranz Auth/Sale (POST /Api/Auth or
/// /Api/Sale): no browser and no 3-D Secure (Click to Pay already authenticated the consumer).
/// Verified against PowerTranz staging: a card without CVV and ThreeDSecure=false is approved with IsoResponseCode 00.
/// The account number (PAN or network token) goes in Source.CardPan with its expiry as YYMM; a dynamic security
/// code (DTVC) goes in Source.CardCvv; the DSRP cryptogram and ECI go in the configured Source fields.
/// </summary>
public sealed class PowerTranzPaymentProcessor(
    HttpClient httpClient,
    IOptions<PowerTranzOptions> options,
    TimeProvider time,
    ILogger<PowerTranzPaymentProcessor> logger) : IPaymentProcessor
{
    public string Name => "PowerTranz";

    public bool IsSimulated => false;

    public async Task<PaymentResult> ProcessAsync(PaymentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = options.Value;
        if (!settings.IsConfigured)
        {
            throw new PaymentProcessingException("PowerTranz is not configured (PowerTranz:PowerTranzId / PowerTranzPassword).");
        }

        var transactionId = Guid.NewGuid().ToString();
        var body = BuildRequest(request, transactionId, settings);
        var path = $"Api/{(settings.Transaction == "Sale" ? "Sale" : "Auth")}";
        var uri = new Uri(new Uri(settings.BaseUrl.TrimEnd('/') + "/"), path);

        using var message = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        message.Headers.Add("PowerTranz-PowerTranzId", settings.PowerTranzId);
        message.Headers.Add("PowerTranz-PowerTranzPassword", settings.PowerTranzPassword);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(message, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new PaymentProcessingException($"Could not reach PowerTranz at {uri.Host}.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PaymentProcessingException($"PowerTranz did not answer within {httpClient.Timeout.TotalSeconds:0}s.", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonObject? json;
            try
            {
                json = JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException)
            {
                json = null;
            }

            if (!response.IsSuccessStatusCode || json is null)
            {
                throw new PaymentProcessingException(
                    $"PowerTranz /{path} failed with HTTP {(int)response.StatusCode}. {Describe(json, text)}");
            }

            var approved = json["Approved"]?.GetValue<bool>() == true;
            var errors = Errors(json);
            if (!approved && errors.Length > 0)
            {
                // Validation errors (e.g. invalid field) are not a card decline: report them as a failure.
                throw new PaymentProcessingException($"PowerTranz rejected the request: {errors}");
            }

            var result = new PaymentResult
            {
                Approved = approved,
                TransactionId = json["TransactionIdentifier"]?.GetValue<string>() ?? transactionId,
                AuthorizationCode = json["AuthorizationCode"]?.GetValue<string>(),
                ResponseCode = json["IsoResponseCode"]?.GetValue<string>() ?? string.Empty,
                ResponseMessage = json["ResponseMessage"]?.GetValue<string>() ?? string.Empty,
                ProcessedAt = time.GetUtcNow()
            };
            logger.LogInformation("PowerTranz {Path} for order {OrderId}: {ResponseCode} {ResponseMessage}.",
                path, request.OrderId, result.ResponseCode, result.ResponseMessage);
            return result;
        }
    }

    private JsonObject BuildRequest(PaymentRequest request, string transactionId, PowerTranzOptions settings)
    {
        var source = new JsonObject
        {
            ["CardPan"] = request.AccountNumber,
            ["CardExpiration"] = request.Expiration
        };
        if (!string.IsNullOrWhiteSpace(request.CardholderName))
        {
            source["CardholderName"] = request.CardholderName;
        }

        // Dynamic card security code (DTVC) replaces the CVC2.
        Add(source, "CardCvv", request.SecurityCode);

        // DSRP cryptogram: with a network token, or with the PAN of a "DSRP + PAN" payload.
        if (!string.IsNullOrWhiteSpace(request.Cryptogram))
        {
            AddIfConfigured(source, settings.CryptogramSourceField, request.Cryptogram);
            AddIfConfigured(source, settings.EciSourceField, request.Eci);
            if (string.IsNullOrWhiteSpace(settings.CryptogramSourceField))
            {
                logger.LogWarning("Order {OrderId}: {CredentialType} sent as CardPan without its cryptogram " +
                    "(PowerTranz:CryptogramSourceField is not set).", request.OrderId, request.CredentialType);
            }
        }

        var body = new JsonObject
        {
            ["TransactionIdentifier"] = transactionId,
            ["TotalAmount"] = decimal.Round(request.Amount, 2),
            ["CurrencyCode"] = PowerTranzCodes.Currency(request.CurrencyCode),
            ["ThreeDSecure"] = false,
            ["OrderIdentifier"] = request.OrderId,
            ["AddressMatch"] = false,
            ["Source"] = source
        };

        if (request.BillingAddress is { } billing)
        {
            var address = new JsonObject();
            Add(address, "FirstName", billing.FirstName);
            Add(address, "LastName", billing.LastName);
            Add(address, "Line1", billing.Line1);
            Add(address, "Line2", billing.Line2);
            Add(address, "City", billing.City);
            Add(address, "State", billing.State);
            Add(address, "PostalCode", billing.PostalCode);
            Add(address, "CountryCode", PowerTranzCodes.Country(billing.CountryCode));
            Add(address, "EmailAddress", billing.EmailAddress);
            Add(address, "PhoneNumber", billing.PhoneNumber);
            body["BillingAddress"] = address;
        }

        return body;
    }

    private static void Add(JsonObject target, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            target[name] = value;
        }
    }

    private static void AddIfConfigured(JsonObject target, string fieldName, string? value)
    {
        if (!string.IsNullOrWhiteSpace(fieldName))
        {
            Add(target, fieldName, value);
        }
    }

    private static string Errors(JsonObject json) =>
        json["Errors"] is JsonArray { Count: > 0 } errors
            ? string.Join("; ", errors.Select(e => $"[{e?["Code"]}] {e?["Message"]}"))
            : string.Empty;

    // Error bodies may echo request data: only PowerTranz codes and messages are reported.
    private static string Describe(JsonObject? json, string text)
    {
        if (json is null)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Body ({text.Length} chars) not shown.");
        }

        var errors = Errors(json);
        return errors.Length > 0 ? errors : $"[{json["IsoResponseCode"]}] {json["ResponseMessage"]}";
    }
}
