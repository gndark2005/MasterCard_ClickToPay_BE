using System.Text.Json.Serialization;

namespace MC_ClickToPay.PaymentDemo.Api.Endpoints;

public sealed class SamplePayloadResponse
{
    [JsonPropertyName("encryptedPayload")]
    public required string EncryptedPayload { get; init; }

    /// <summary>Which key encrypted it, and therefore how long it stays decryptable.</summary>
    [JsonPropertyName("note")]
    public required string Note { get; init; }
}
