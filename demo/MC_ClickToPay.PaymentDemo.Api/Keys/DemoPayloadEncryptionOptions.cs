namespace MC_ClickToPay.PaymentDemo.Api.Keys;

public sealed class DemoPayloadEncryptionOptions
{
    public const string SectionName = "PayloadEncryption";

    /// <summary>
    /// The Mastercard Payload Encryption private key: a PEM file (.pem, as Mastercard Developers provides it) or a
    /// .p12/.pfx. Required to decrypt real Click to Pay payloads. When set, it always takes precedence over
    /// <see cref="UseEphemeralDevelopmentKey"/>.
    /// </summary>
    public string CertificatePath { get; set; } = string.Empty;

    /// <summary>Only for a password-protected .p12/.pfx; not used for .pem.</summary>
    public string? CertificatePassword { get; set; }

    /// <summary>
    /// Local testing only: generates an in-memory RSA key when the API starts. Only payloads created by the sample
    /// endpoint (or encrypted with that key) can be decrypted, and they stop working when the API restarts.
    /// </summary>
    public bool UseEphemeralDevelopmentKey { get; set; }
}
