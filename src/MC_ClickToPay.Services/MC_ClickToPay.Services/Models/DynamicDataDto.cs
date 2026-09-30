using System.Text.Json.Serialization;

namespace MC_ClickToPay.Services.Models;

public sealed class DynamicDataDto
{
    [JsonPropertyName("dynamicDataValue")]
    public string? DynamicDataValue { get; init; }

    [JsonPropertyName("dynamicDataType")]
    public string? DynamicDataType { get; init; }
}
