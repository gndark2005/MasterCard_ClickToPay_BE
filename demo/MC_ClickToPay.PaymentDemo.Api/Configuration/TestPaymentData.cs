using System.Text.Json.Serialization;
using MC_ClickToPay.Services.Models;

namespace MC_ClickToPay.PaymentDemo.Api.Configuration;

/// <summary>
/// Test card/payment data in the shape of a decrypted Mastercard payload. Bound from PaymentDemo:TestPayment and
/// also accepted as the (optional) body of the sample endpoint, where any non-null field overrides the configuration.
/// Never put real card data here.
/// </summary>
public sealed class TestPaymentData
{
    [JsonPropertyName("paymentToken")]
    public string? PaymentToken { get; set; }

    [JsonPropertyName("tokenExpirationMonth")]
    public string? TokenExpirationMonth { get; set; }

    [JsonPropertyName("tokenExpirationYear")]
    public string? TokenExpirationYear { get; set; }

    [JsonPropertyName("paymentAccountReference")]
    public string? PaymentAccountReference { get; set; }

    [JsonPropertyName("cardholderFullName")]
    public string? CardholderFullName { get; set; }

    [JsonPropertyName("cryptogram")]
    public string? Cryptogram { get; set; }

    [JsonPropertyName("cryptogramType")]
    public string? CryptogramType { get; set; }

    [JsonPropertyName("consumerEmailAddress")]
    public string? ConsumerEmailAddress { get; set; }

    [JsonPropertyName("consumerFirstName")]
    public string? ConsumerFirstName { get; set; }

    [JsonPropertyName("consumerLastName")]
    public string? ConsumerLastName { get; set; }

    [JsonPropertyName("billingAddress")]
    public PaymentAddressDto? BillingAddress { get; set; }

    [JsonPropertyName("shippingAddress")]
    public PaymentAddressDto? ShippingAddress { get; set; }

    [JsonPropertyName("consumerMobileNumber")]
    public ConsumerMobileNumberDto? ConsumerMobileNumber { get; set; }

    /// <summary>
    /// The configured data with <paramref name="overrides"/> applied, as Mastercard would encrypt it: a tokenized
    /// payload (token + cryptogram).
    /// </summary>
    public TokenizedPayloadDto ToDecryptedPayload(TestPaymentData? overrides = null) => new()
    {
        Token = new PaymentTokenDto
        {
            PaymentToken = overrides?.PaymentToken ?? PaymentToken,
            TokenExpirationMonth = overrides?.TokenExpirationMonth ?? TokenExpirationMonth,
            TokenExpirationYear = overrides?.TokenExpirationYear ?? TokenExpirationYear,
            PaymentAccountReference = overrides?.PaymentAccountReference ?? PaymentAccountReference,
            CardholderFullName = overrides?.CardholderFullName ?? CardholderFullName
        },
        DynamicData = new DynamicDataDto
        {
            DynamicDataValue = overrides?.Cryptogram ?? Cryptogram,
            DynamicDataType = overrides?.CryptogramType ?? CryptogramType ?? DynamicDataTypes.Cryptogram
        },
        BillingAddress = overrides?.BillingAddress ?? BillingAddress,
        ShippingAddress = overrides?.ShippingAddress ?? ShippingAddress,
        ConsumerMobileNumber = overrides?.ConsumerMobileNumber ?? ConsumerMobileNumber,
        ConsumerEmailAddress = overrides?.ConsumerEmailAddress ?? ConsumerEmailAddress,
        ConsumerFirstName = overrides?.ConsumerFirstName ?? ConsumerFirstName,
        ConsumerLastName = overrides?.ConsumerLastName ?? ConsumerLastName
    };
}
