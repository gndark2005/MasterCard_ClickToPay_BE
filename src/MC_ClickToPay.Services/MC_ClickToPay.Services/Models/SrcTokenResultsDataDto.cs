using System.Text.Json.Serialization;

namespace MC_ClickToPay.Services.Models;

public sealed class SrcTokenResultsDataDto
{
    [JsonPropertyName("unpredictableNumber")]
    public string? UnpredictableNumber { get; init; }

    [JsonPropertyName("tokenRequesterId")]
    public string? TokenRequesterId { get; init; }
}
