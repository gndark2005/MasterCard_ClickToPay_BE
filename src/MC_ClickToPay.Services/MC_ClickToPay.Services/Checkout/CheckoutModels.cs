using System.Text.Json.Serialization;
using MC_ClickToPay.Services.Models;

namespace MC_ClickToPay.Services.Checkout;

/// <summary>
/// What the UI sends after checkoutWithCard(): the Mastercard values (checkoutResponseData and headers) plus the
/// PowerTranz SPI token of its payment. The SPI token is not used here (the UI calls PowerTranz itself with the
/// decrypted card); it is accepted for traceability and is never logged.
/// </summary>
public sealed class CompleteCheckoutRequest
{
    [JsonPropertyName("spiToken")]
    public string SpiToken { get; init; } = string.Empty;

    /// <summary>Must match the configured MastercardApi:SrcDpaId (the keys belong to that DPA).</summary>
    [JsonPropertyName("srcDpaId")]
    public string SrcDpaId { get; init; } = string.Empty;

    /// <summary>checkoutResponseData.srcCorrelationId returned by checkoutWithCard().</summary>
    [JsonPropertyName("srcCorrelationId")]
    public string SrcCorrelationId { get; init; } = string.Empty;

    [JsonPropertyName("merchantTransactionId")]
    public string MerchantTransactionId { get; init; } = string.Empty;

    /// <summary>x-src-cx-flow-id header returned by checkoutWithCard().</summary>
    [JsonPropertyName("flowId")]
    public string FlowId { get; init; } = string.Empty;

    /// <summary>The UI's own tracing id: logged and echoed in the X-Correlation-Id response header, not sent to Mastercard.</summary>
    [JsonPropertyName("xCorrelationId")]
    public string XCorrelationId { get; init; } = string.Empty;
}

public sealed class CompleteCheckoutResult
{
    [JsonPropertyName("merchantTransactionId")]
    public string? MerchantTransactionId { get; init; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    /// <summary>assuranceData.eci from the /checkout response (it is not inside the encrypted payload).</summary>
    [JsonPropertyName("eci")]
    public string? Eci { get; init; }

    /// <summary>The decrypted payload; its concrete model follows dynamicData.dynamicDataType.</summary>
    [JsonPropertyName("payload")]
    public required DecryptedPayloadDto Payload { get; init; }
}

public sealed class CheckoutConfirmationRequest
{
    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; init; } = string.Empty;

    [JsonPropertyName("merchantTransactionId")]
    public string MerchantTransactionId { get; init; } = string.Empty;

    [JsonPropertyName("flowId")]
    public string? FlowId { get; init; }

    [JsonPropertyName("transactionAmount")]
    public decimal TransactionAmount { get; init; }

    [JsonPropertyName("transactionCurrencyCode")]
    public string TransactionCurrencyCode { get; init; } = string.Empty;

    [JsonPropertyName("approved")]
    public bool Approved { get; init; }

    [JsonPropertyName("confirmationReason")]
    public string? ConfirmationReason { get; init; }
}
