using System.Text.Json.Serialization;

namespace MC_ClickToPay.Services.Models;

public sealed class ConsumerMobileNumberDto
{
    [JsonPropertyName("countryCode")]
    public string? CountryCode { get; init; }

    [JsonPropertyName("phoneNumber")]
    public string? PhoneNumber { get; init; }
}
