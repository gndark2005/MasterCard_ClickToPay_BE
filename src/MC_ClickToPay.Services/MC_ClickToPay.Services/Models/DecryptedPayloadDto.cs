using System.Text.Json.Serialization;

namespace MC_ClickToPay.Services.Models;

// Classes deliberately avoid record-generated ToString() exposing payment data.
public sealed class DecryptedPayloadDto
{
    [JsonPropertyName("token")]
    public PaymentTokenDto? Token { get; init; }

    [JsonPropertyName("dynamicData")]
    public DynamicDataDto? DynamicData { get; init; }

    [JsonPropertyName("srcTokenResultsData")]
    public SrcTokenResultsDataDto? SrcTokenResultsData { get; init; }

    [JsonPropertyName("shippingAddress")]
    public PaymentAddressDto? ShippingAddress { get; init; }

    [JsonPropertyName("billingAddress")]
    public PaymentAddressDto? BillingAddress { get; init; }

    [JsonPropertyName("consumerEmailAddress")]
    public string? ConsumerEmailAddress { get; init; }

    [JsonPropertyName("consumerFirstName")]
    public string? ConsumerFirstName { get; init; }

    [JsonPropertyName("consumerLastName")]
    public string? ConsumerLastName { get; init; }

    [JsonPropertyName("consumerFullName")]
    public string? ConsumerFullName { get; init; }

    [JsonPropertyName("consumerMobileNumber")]
    public ConsumerMobileNumberDto? ConsumerMobileNumber { get; init; }
}
