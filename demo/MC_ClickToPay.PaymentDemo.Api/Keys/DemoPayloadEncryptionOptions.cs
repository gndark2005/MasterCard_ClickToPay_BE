namespace MC_ClickToPay.PaymentDemo.Api.Keys;

public sealed class DemoPayloadEncryptionOptions
{
    public const string SectionName = "PayloadEncryption";

    /// <summary>
    /// The Mastercard Payload Encryption .p12/.pfx (private key). Required to decrypt real Click to Pay payloads.
    /// When set, it always takes precedence over <see cref="UseEphemeralDevelopmentKey"/>.
    /// </summary>
    public string CertificatePath { get; set; } = string.Empty;

    public string? CertificatePassword { get; set; }

    /// <summary>
    /// Local testing only: generates an in-memory RSA key when the API starts. Only payloads created by the sample
    /// endpoint (or encrypted with that key) can be decrypted, and they stop working when the API restarts.
    /// </summary>
    public bool UseEphemeralDevelopmentKey { get; set; }
}
