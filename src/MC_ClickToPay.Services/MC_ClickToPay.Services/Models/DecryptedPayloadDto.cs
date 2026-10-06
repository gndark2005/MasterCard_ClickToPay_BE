using System.Text.Json.Serialization;

namespace MC_ClickToPay.Services.Models;

/// <summary>
/// Fields common to every decrypted Mastercard payload. The concrete model is chosen from
/// dynamicData.dynamicDataType by <see cref="DecryptedPayloadJsonConverter"/>:
/// <see cref="TokenizedPayloadDto"/>, <see cref="DsrpPanPayloadDto"/>, <see cref="DynamicSecurityCodePayloadDto"/> or
/// <see cref="FpanPayloadDto"/>. Classes, not records, so ToString() never prints payment data.
/// </summary>
[JsonConverter(typeof(DecryptedPayloadJsonConverter))]
public abstract class DecryptedPayloadDto
{
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

    /// <summary>The cardholder name of the credential (token or card), when Mastercard sends one.</summary>
    public abstract string? GetCredentialCardholderName();

    /// <summary>The Payment Account Reference of the credential, when Mastercard sends one.</summary>
    public abstract string? GetCredentialPaymentAccountReference();
}
