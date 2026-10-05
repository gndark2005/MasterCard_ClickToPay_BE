using System.Text.Json.Serialization;

namespace MC_ClickToPay.PaymentDemo.Api.Endpoints;

public sealed class ConfirmPaymentRequest
{
    /// <summary>The five-part compact JWE (Mastercard encryptedPayload, or one created by the sample endpoint).</summary>
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
}
