using System.Text.Json.Serialization;

namespace MC_ClickToPay.Services.Models;

public sealed class PaymentTokenDto
{
    [JsonPropertyName("paymentToken")]
    public string? PaymentToken { get; init; }

    [JsonPropertyName("tokenExpirationMonth")]
    public string? TokenExpirationMonth { get; init; }

    [JsonPropertyName("tokenExpirationYear")]
    public string? TokenExpirationYear { get; init; }

    [JsonPropertyName("paymentAccountReference")]
    public string? PaymentAccountReference { get; init; }
}
