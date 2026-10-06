namespace MC_ClickToPay.Api.Keys;

public sealed class PayloadEncryptionOptions
{
    /// <summary>
    /// The Payload Encryption private key: a PEM file (.pem, as Mastercard Developers provides it) or a .p12/.pfx.
    /// </summary>
    public string CertificatePath { get; set; } = string.Empty;

    /// <summary>Only for a password-protected .p12/.pfx; not used for .pem.</summary>
    public string? CertificatePassword { get; set; }
}
