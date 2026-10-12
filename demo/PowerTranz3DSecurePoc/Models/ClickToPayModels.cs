using System.Text.Json.Serialization;

namespace PowerTranz3DSecurePoc.Models;

/// <summary>What the Click to Pay page posts after checkoutWithCard() completes.</summary>
public sealed record ClickToPayCheckout(string CorrelationId, string MerchantTransactionId, string? FlowId);

// Response of MasterCard_ClickToPay_BE POST /api/checkout: flat fields, null when Mastercard did not send them.
public sealed class ClickToPayCompleteResult
{
    [JsonPropertyName("cardNo")] public string? CardNo { get; set; }
    // YYMM.
    [JsonPropertyName("cardExpDate")] public string? CardExpDate { get; set; }
    [JsonPropertyName("cardCVV2")] public string? CardCvv2 { get; set; }
    [JsonPropertyName("cardholderName")] public string? CardholderName { get; set; }
    [JsonPropertyName("billToFirstName")] public string? BillToFirstName { get; set; }
    [JsonPropertyName("billToLastName")] public string? BillToLastName { get; set; }
    [JsonPropertyName("billToAddress1")] public string? BillToAddress1 { get; set; }
    [JsonPropertyName("billToAddress2")] public string? BillToAddress2 { get; set; }
    [JsonPropertyName("billToState")] public string? BillToState { get; set; }
    [JsonPropertyName("billToPostCode")] public string? BillToPostCode { get; set; }
    [JsonPropertyName("billToCountry")] public string? BillToCountry { get; set; }
    [JsonPropertyName("billToTelephone")] public string? BillToTelephone { get; set; }
    [JsonPropertyName("billToEmail")] public string? BillToEmail { get; set; }
    [JsonPropertyName("shipToFirstName")] public string? ShipToFirstName { get; set; }
    [JsonPropertyName("shipToLastName")] public string? ShipToLastName { get; set; }
    [JsonPropertyName("shipToAddress1")] public string? ShipToAddress1 { get; set; }
    [JsonPropertyName("shipToAddress2")] public string? ShipToAddress2 { get; set; }
    [JsonPropertyName("shipToState")] public string? ShipToState { get; set; }
    [JsonPropertyName("shipToPostCode")] public string? ShipToPostCode { get; set; }
    [JsonPropertyName("shipToCountry")] public string? ShipToCountry { get; set; }
    [JsonPropertyName("shipToTelephone")] public string? ShipToTelephone { get; set; }
    [JsonPropertyName("shipToEmail")] public string? ShipToEmail { get; set; }
}
