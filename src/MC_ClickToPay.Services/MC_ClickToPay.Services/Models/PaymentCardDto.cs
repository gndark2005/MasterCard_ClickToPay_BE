using System.Text.Json.Serialization;

namespace MC_ClickToPay.Services.Models;

/// <summary>
/// FPAN payment credential: present when the checkout asked for dynamicDataType NONE (or, with dual payloads,
/// next to the token). A class, not a record, so ToString() never prints the card number.
/// </summary>
public sealed class PaymentCardDto
{
    [JsonPropertyName("primaryAccountNumber")]
    public string? PrimaryAccountNumber { get; init; }

    [JsonPropertyName("panExpirationMonth")]
    public string? PanExpirationMonth { get; init; }

    [JsonPropertyName("panExpirationYear")]
    public string? PanExpirationYear { get; init; }

    [JsonPropertyName("cardholderFullName")]
    public string? CardholderFullName { get; init; }

    [JsonPropertyName("paymentAccountReference")]
    public string? PaymentAccountReference { get; init; }
}
