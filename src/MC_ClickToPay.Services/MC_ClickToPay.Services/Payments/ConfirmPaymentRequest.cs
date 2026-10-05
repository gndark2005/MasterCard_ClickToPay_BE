using System.Text.Json.Serialization;

namespace MC_ClickToPay.Services.Payments;

/// <summary>Body of the payment confirmation endpoints (MC_ClickToPay.Api and the payment demo).</summary>
public sealed class ConfirmPaymentRequest
{
    /// <summary>The five-part compact JWE: Mastercard /checkout encryptedPayload.</summary>
    [JsonPropertyName("encryptedPayload")]
    public string? EncryptedPayload { get; init; }

    [JsonPropertyName("transactionAmount")]
    public decimal? TransactionAmount { get; init; }

    /// <summary>ISO 4217: alphabetic like Mastercard ("USD") or numeric like PowerTranz ("840").</summary>
    [JsonPropertyName("transactionCurrencyCode")]
    public string? TransactionCurrencyCode { get; init; }

    /// <summary>Optional merchant order id (letters, digits, '-' and '_'; up to 50). Generated when omitted.</summary>
    [JsonPropertyName("orderId")]
    public string? OrderId { get; init; }

    /// <summary>Optional assuranceData.eci from Mastercard /checkout (2 digits, e.g. "02", "06").</summary>
    [JsonPropertyName("eci")]
    public string? Eci { get; init; }
}
