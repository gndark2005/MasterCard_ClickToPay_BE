using System.Text.Json.Serialization;
using MC_ClickToPay.Services.Checkout;

namespace MC_ClickToPay.Api.Endpoints.Checkout;

/// <summary>Response of POST /api/checkout: the decrypted payload, always with a card object.</summary>
public sealed class CompleteCheckoutResponse
{
    [JsonPropertyName("merchantTransactionId")]
    public string? MerchantTransactionId { get; init; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    /// <summary>assuranceData.eci from the Mastercard /checkout response (not inside the encrypted payload).</summary>
    [JsonPropertyName("eci")]
    public string? Eci { get; init; }

    /// <summary>
    /// "Pan": payload.card.primaryAccountNumber is the card number Mastercard sent. "NetworkToken": Mastercard sent
    /// only a token, and payload.card was filled from it (it is the token number, not the real PAN).
    /// </summary>
    [JsonPropertyName("credentialType")]
    public required string CredentialType { get; init; }

    /// <summary>dynamicData.dynamicDataType of the decrypted payload.</summary>
    [JsonPropertyName("dynamicDataType")]
    public string? DynamicDataType { get; init; }

    [JsonPropertyName("payload")]
    public required CheckoutPanPayloadDto Payload { get; init; }

    public static CompleteCheckoutResponse From(CompleteCheckoutResult result)
    {
        var payload = CheckoutPanPayloadDto.From(result.Payload);
        return new CompleteCheckoutResponse
        {
            MerchantTransactionId = result.MerchantTransactionId,
            CorrelationId = result.CorrelationId,
            Eci = result.Eci,
            CredentialType = payload.CredentialType.ToString(),
            DynamicDataType = result.Payload.DynamicData?.DynamicDataType,
            Payload = payload
        };
    }
}
