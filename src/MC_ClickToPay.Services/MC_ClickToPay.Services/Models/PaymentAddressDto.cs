using System.Text.Json.Serialization;

namespace MC_ClickToPay.Services.Models;

public sealed class PaymentAddressDto
{
    [JsonPropertyName("addressId")]
    public string? AddressId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("line1")]
    public string? Line1 { get; init; }

    [JsonPropertyName("line2")]
    public string? Line2 { get; init; }

    [JsonPropertyName("line3")]
    public string? Line3 { get; init; }

    [JsonPropertyName("city")]
    public string? City { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("countryCode")]
    public string? CountryCode { get; init; }

    [JsonPropertyName("zip")]
    public string? Zip { get; init; }
}
