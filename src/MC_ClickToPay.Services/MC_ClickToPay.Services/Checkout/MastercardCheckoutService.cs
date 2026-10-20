using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mastercard.Developer.OAuth1Signer.Core;
using MC_ClickToPay.Services.Abstractions;
using MC_ClickToPay.Services.Exceptions;
using MC_ClickToPay.Services.Models;
using Microsoft.Extensions.Options;

namespace MC_ClickToPay.Services.Checkout;

/// <summary>
/// Server-side Click to Pay calls, signed with OAuth 1.0a (RSA-SHA256) as Mastercard requires:
/// POST /srci/api/checkout returns the encryptedPayload for a checkoutWithCard() result, and
/// POST /srci/api/checkout/confirmations reports the authorization outcome.
/// </summary>
public sealed class MastercardCheckoutService(
    HttpClient httpClient,
    IOptions<MastercardCheckoutOptions> options,
    IMastercardSigningKeyProvider signingKeyProvider,
    IPayloadDecryptionService decryption) : IMastercardCheckoutService
{
    private const string CheckoutPath = "srci/api/checkout";
    private const string ConfirmationsPath = "srci/api/checkout/confirmations";

    public async Task<CompleteCheckoutResult> CompleteAsync(
        CompleteCheckoutRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = options.Value;

        var body = new JsonObject
        {
            ["srcDpaId"] = request.SrcDpaId,
            ["correlationId"] = request.SrcCorrelationId,
            ["checkoutType"] = "CLICK_TO_PAY",
            ["checkoutReference"] = new JsonObject
            {
                ["type"] = "MERCHANT_TRANSACTION_ID",
                ["data"] = new JsonObject { ["merchantTransactionId"] = request.MerchantTransactionId },
            },

            ["dpaTransactionOptions"] = new JsonObject
            {
                ["transactionAmount"] = Amount(request.TransactionAmount, request.TransactionCurrencyCode),
                ["paymentOptions"] = new JsonArray(new JsonObject { ["dynamicDataType"] = "CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM" }),
            },
        };

        var response = await SendAsync(CheckoutPath, body, request.FlowId, cancellationToken);
        var json = JsonNode.Parse(response.Body)
            ?? throw new MastercardCheckoutException("Mastercard /checkout returned an empty body.");

        var encryptedPayload = json["encryptedPayload"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(encryptedPayload))
        {
            throw new MastercardCheckoutException("Mastercard /checkout did not return an encryptedPayload.");
        }

        DecryptedPayloadDto payload = await decryption.DecryptAsync(
            new DecryptPayloadRequest { EncryptedPayload = encryptedPayload }, cancellationToken);

        return new CompleteCheckoutResult
        {
            MerchantTransactionId = json["merchantTransactionId"]?.GetValue<string>(),
            CorrelationId = json["correlationId"]?.GetValue<string>(),
            Eci = json["assuranceData"]?["eci"]?.GetValue<string>(),
            Payload = payload,
        };
    }

    public async Task<CheckoutConfirmationResult> ConfirmAsync(
        CheckoutConfirmationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var data = request.ConfirmationData ?? throw new ArgumentException("confirmationData is required.", nameof(request));

        // Body as in the Mastercard spec; optional fields are omitted when not given.
        var confirmation = new JsonObject
        {
            ["checkoutEventType"] = data.CheckoutEventType,
            ["checkoutEventStatus"] = data.CheckoutEventStatus,
            ["confirmationStatus"] = data.ConfirmationStatus,
        };
        AddIfPresent(confirmation, "confirmationReason", data.ConfirmationReason);
        confirmation["confirmationTimestamp"] = (data.ConfirmationTimestamp ?? DateTimeOffset.UtcNow).UtcDateTime
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        AddIfPresent(confirmation, "networkAuthorizationCode", data.NetworkAuthorizationCode);
        AddIfPresent(confirmation, "networkTransactionIdentifier", data.NetworkTransactionIdentifier);
        if (data.TransactionAmount is { } amount)
        {
            confirmation["transactionAmount"] = Amount(amount.Amount, amount.CurrencyCode);
        }

        var body = new JsonObject { ["correlationId"] = request.CorrelationId };
        AddIfPresent(body, "merchantTransactionId", request.MerchantTransactionId);
        body["confirmationData"] = confirmation;

        var headers = new Dictionary<string, string?> { ["merchant-transaction-id"] = request.MerchantTransactionId };
        var response = await SendAsync(ConfirmationsPath, body, request.FlowId, cancellationToken, headers);
        return new CheckoutConfirmationResult { FlowId = response.FlowId ?? request.FlowId };
    }

    private static void AddIfPresent(JsonObject target, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            target[name] = value;
        }
    }

    private static JsonObject Amount(decimal amount, string currencyCode) => new()
    {
        ["transactionAmount"] = decimal.Round(amount, 2).ToString("0.00", CultureInfo.InvariantCulture),
        ["transactionCurrencyCode"] = currencyCode,
    };

    private async Task<MastercardResponse> SendAsync(
        string path, JsonObject body, string? flowId, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? extraHeaders = null)
    {
        var settings = options.Value;
        if (!settings.IsConfigured)
        {
            throw new MastercardCheckoutException("Mastercard API is not configured (ConsumerKey).");
        }

        var uri = new Uri(new Uri(settings.BaseUrl.TrimEnd('/') + "/"), path);
        var payload = body.ToJsonString();

        using var signingKey = await signingKeyProvider.GetSigningKeyAsync(cancellationToken);
        var authorization = OAuth.GetAuthorizationHeader(
            uri.ToString(), "POST", payload, Encoding.UTF8, settings.ConsumerKey, signingKey);

        using var message = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        message.Headers.TryAddWithoutValidation("Authorization", authorization);
        message.Headers.Add("x-openapi-clientid", settings.ClientId);
        if (!string.IsNullOrWhiteSpace(flowId))
        {
            message.Headers.Add("x-src-cx-flow-id", flowId);
        }

        foreach (var (name, value) in extraHeaders ?? new Dictionary<string, string?>())
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                message.Headers.TryAddWithoutValidation(name, value);
            }
        }

        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(message, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new MastercardCheckoutException($"Could not reach Mastercard at {uri.Host}.", null, ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            var statusCode = (int)response.StatusCode;
            if (statusCode is < 200 or > 299)
            {
                throw new MastercardCheckoutException(
                    $"Mastercard /{path} failed with HTTP {statusCode}. {DescribeError(text)}", statusCode);
            }

            var responseFlowId = response.Headers.TryGetValues("X-Src-Cx-Flow-Id", out var values)
                ? values.FirstOrDefault()
                : null;
            return new MastercardResponse(text, responseFlowId);
        }
    }

    private sealed record MastercardResponse(string Body, string? FlowId);

    // Mastercard error bodies hold reason codes and descriptions only; anything else is reported by size.
    private static string DescribeError(string body)
    {
        try
        {
            var json = JsonNode.Parse(body) as JsonObject;

            // Shapes seen: {"errors":[{"reasonCode","description"}]}, {"Errors":{"Error":[{"ReasonCode","Description"}]}},
            // {"reason","message"}.
            var errors = json?["errors"] ?? json?["Errors"] ?? json?["error"];
            if (errors is JsonObject wrapper)
            {
                errors = wrapper["Error"] ?? wrapper["error"];
            }

            var error = errors is JsonArray { Count: > 0 } list ? list[0] as JsonObject : errors as JsonObject;
            var reason = error?["reasonCode"] ?? error?["ReasonCode"] ?? json?["reason"] ?? json?["errorCode"];
            var description = error?["description"] ?? error?["Description"] ?? json?["message"];
            if (reason is not null || description is not null)
            {
                return $"[{reason}] {description}";
            }

            if (json is not null)
            {
                return $"Body fields: {string.Join(", ", json.Select(p => p.Key))}.";
            }
        }
        catch (JsonException)
        {
            // Not JSON; fall through.
        }

        return $"Body ({body.Length} chars) not shown.";
    }
}
