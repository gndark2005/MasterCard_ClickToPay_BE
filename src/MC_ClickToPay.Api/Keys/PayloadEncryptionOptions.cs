namespace MC_ClickToPay.Api.Keys;

public sealed class PayloadEncryptionOptions
{
    public string CertificatePath { get; set; } = string.Empty;

    public string? CertificatePassword { get; set; }
}
