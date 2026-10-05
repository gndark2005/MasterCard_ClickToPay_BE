using System.Text.Json.Serialization;

namespace PowerTranz3DSecurePoc.Models;

/// <summary>What the Click to Pay page posts after checkoutWithCard() completes.</summary>
public sealed record ClickToPayCheckout(string CorrelationId, string MerchantTransactionId, string? FlowId);

// Response of MasterCard_ClickToPay_BE POST /api/checkout/complete (camelCase JSON).
public sealed class ClickToPayCompleteResult
{
    [JsonPropertyName("merchantTransactionId")] public string? MerchantTransactionId { get; set; }
    [JsonPropertyName("correlationId")] public string? CorrelationId { get; set; }
    [JsonPropertyName("eci")] public string? Eci { get; set; }
    [JsonPropertyName("payload")] public ClickToPayPayload? Payload { get; set; }
}

public sealed class ClickToPayPayload
{
    [JsonPropertyName("token")] public ClickToPayToken? Token { get; set; }
    [JsonPropertyName("dynamicData")] public ClickToPayDynamicData? DynamicData { get; set; }
    [JsonPropertyName("billingAddress")] public ClickToPayAddress? BillingAddress { get; set; }
    [JsonPropertyName("shippingAddress")] public ClickToPayAddress? ShippingAddress { get; set; }
    [JsonPropertyName("consumerEmailAddress")] public string? ConsumerEmailAddress { get; set; }
    [JsonPropertyName("consumerFirstName")] public string? ConsumerFirstName { get; set; }
    [JsonPropertyName("consumerLastName")] public string? ConsumerLastName { get; set; }
    [JsonPropertyName("consumerFullName")] public string? ConsumerFullName { get; set; }
    [JsonPropertyName("consumerMobileNumber")] public ClickToPayPhone? ConsumerMobileNumber { get; set; }
}

public sealed class ClickToPayToken
{
    [JsonPropertyName("paymentToken")] public string? PaymentToken { get; set; }
    [JsonPropertyName("tokenExpirationMonth")] public string? TokenExpirationMonth { get; set; }
    [JsonPropertyName("tokenExpirationYear")] public string? TokenExpirationYear { get; set; }
    [JsonPropertyName("paymentAccountReference")] public string? PaymentAccountReference { get; set; }
    [JsonPropertyName("cardholderFullName")] public string? CardholderFullName { get; set; }
}

public sealed class ClickToPayDynamicData
{
    [JsonPropertyName("dynamicDataValue")] public string? DynamicDataValue { get; set; }
    [JsonPropertyName("dynamicDataType")] public string? DynamicDataType { get; set; }
}

public sealed class ClickToPayAddress
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("line1")] public string? Line1 { get; set; }
    [JsonPropertyName("line2")] public string? Line2 { get; set; }
    [JsonPropertyName("city")] public string? City { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("countryCode")] public string? CountryCode { get; set; }
    [JsonPropertyName("zip")] public string? Zip { get; set; }
}

public sealed class ClickToPayPhone
{
    [JsonPropertyName("countryCode")] public string? CountryCode { get; set; }
    [JsonPropertyName("phoneNumber")] public string? PhoneNumber { get; set; }
}
