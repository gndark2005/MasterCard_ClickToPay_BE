using System.Text.Json.Serialization;

namespace MC_ClickToPay.Services.Payments;

/// <summary>Payment confirmation. Card data is limited to the token's last four digits.</summary>
public sealed class ConfirmPaymentResponse
{
    /// <summary>"Approved" or "Declined".</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("approved")]
    public required bool Approved { get; init; }

    /// <summary>True while the simulated processor is used: no money moved.</summary>
    [JsonPropertyName("simulated")]
    public required bool Simulated { get; init; }

    [JsonPropertyName("processor")]
    public required string Processor { get; init; }

    [JsonPropertyName("orderId")]
    public required string OrderId { get; init; }

    [JsonPropertyName("transactionId")]
    public required string TransactionId { get; init; }

    [JsonPropertyName("authorizationCode")]
    public string? AuthorizationCode { get; init; }

    [JsonPropertyName("responseCode")]
    public required string ResponseCode { get; init; }

    [JsonPropertyName("responseMessage")]
    public required string ResponseMessage { get; init; }

    [JsonPropertyName("transactionAmount")]
    public required decimal TransactionAmount { get; init; }

    [JsonPropertyName("transactionCurrencyCode")]
    public required string TransactionCurrencyCode { get; init; }

    [JsonPropertyName("tokenLast4")]
    public required string TokenLast4 { get; init; }

    [JsonPropertyName("eci")]
    public string? Eci { get; init; }

    [JsonPropertyName("processedAt")]
    public required DateTimeOffset ProcessedAt { get; init; }

    public static ConfirmPaymentResponse From(PaymentConfirmation confirmation) => new()
    {
        Status = confirmation.Result.Approved ? "Approved" : "Declined",
        Approved = confirmation.Result.Approved,
        Simulated = confirmation.Simulated,
        Processor = confirmation.Processor,
        OrderId = confirmation.Request.OrderId,
        TransactionId = confirmation.Result.TransactionId,
        AuthorizationCode = confirmation.Result.AuthorizationCode,
        ResponseCode = confirmation.Result.ResponseCode,
        ResponseMessage = confirmation.Result.ResponseMessage,
        TransactionAmount = confirmation.Request.Amount,
        TransactionCurrencyCode = confirmation.Request.CurrencyCode,
        TokenLast4 = confirmation.Request.TokenLast4,
        Eci = confirmation.Request.Eci,
        ProcessedAt = confirmation.Result.ProcessedAt
    };
}
