namespace MC_ClickToPay.Api.Keys;

/// <summary>OAuth 1.0a signing key downloaded from the Mastercard Developers project.</summary>
public sealed class MastercardSigningKeyOptions
{
    /// <summary>A .pem private key or a .p12 / .pfx keystore.</summary>
    public string SigningKeyPath { get; set; } = string.Empty;

    /// <summary>Only for .p12 / .pfx keystores.</summary>
    public string SigningKeyPassword { get; set; } = string.Empty;
}
