using System.Text.Json.Serialization;
using MC_ClickToPay.Services.Models;

namespace MC_ClickToPay.Services.Checkout;

/// <summary>Values the browser gets from checkoutWithCard() (checkoutResponseData and headers).</summary>
public sealed class CompleteCheckoutRequest
{
    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; init; } = string.Empty;

    [JsonPropertyName("merchantTransactionId")]
    public string MerchantTransactionId { get; init; } = string.Empty;

    /// <summary>Optional x-src-cx-flow-id header returned by checkoutWithCard().</summary>
    [JsonPropertyName("flowId")]
    public string? FlowId { get; init; }

    [JsonPropertyName("transactionAmount")]
    public decimal TransactionAmount { get; init; }

    /// <summary>ISO 4217 alphabetic code, e.g. "USD".</summary>
    [JsonPropertyName("transactionCurrencyCode")]
    public string TransactionCurrencyCode { get; init; } = string.Empty;
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

    [JsonPropertyName("payload")]
    public DecryptedPayloadDto Payload { get; init; } = new();
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
