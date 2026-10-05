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
            ["srcDpaId"] = settings.SrcDpaId,
            ["correlationId"] = request.CorrelationId,
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
        var json = JsonNode.Parse(response)
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

    public async Task ConfirmAsync(CheckoutConfirmationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Status codes as sent by the Mastercard sandbox reference client: "01" = success, "02" = failure.
        var status = request.Approved ? "01" : "02";
        var body = new JsonObject
        {
            ["correlationId"] = request.CorrelationId,
            ["merchantTransactionId"] = request.MerchantTransactionId,
            ["confirmationData"] = new JsonObject
            {
                ["confirmationReason"] = request.ConfirmationReason
                    ?? (request.Approved ? "Order Successfully Created" : "Payment Declined"),
                ["transactionAmount"] = Amount(request.TransactionAmount, request.TransactionCurrencyCode),
                ["confirmationTimestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                ["networkTransactionIdentifier"] = "UNAVLB",
                ["checkoutEventStatus"] = status,
                ["checkoutEventType"] = "01",
                ["confirmationStatus"] = status,
            },
        };

        await SendAsync(ConfirmationsPath, body, request.FlowId, cancellationToken);
    }

    private static JsonObject Amount(decimal amount, string currencyCode) => new()
    {
        ["transactionAmount"] = decimal.Round(amount, 2).ToString("0.00", CultureInfo.InvariantCulture),
        ["transactionCurrencyCode"] = currencyCode,
    };

    private async Task<string> SendAsync(
        string path, JsonObject body, string? flowId, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.IsConfigured)
        {
            throw new MastercardCheckoutException("Mastercard API is not configured (SrcDpaId / ConsumerKey).");
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

            return text;
        }
    }

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
