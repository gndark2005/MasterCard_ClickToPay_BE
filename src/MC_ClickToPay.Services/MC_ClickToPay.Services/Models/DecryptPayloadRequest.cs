namespace MC_ClickToPay.Services.Models;

public sealed class DecryptPayloadRequest
{
    /// <summary>The five-part compact JWE extracted from a verified Checkout response.</summary>
    public required string EncryptedPayload { get; init; }
}
