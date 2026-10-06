namespace MC_ClickToPay.Services.Payments.PowerTranz;

/// <summary>Bound from the "PowerTranz" section. Secrets (PowerTranzId, PowerTranzPassword) go in appsettings.Local.json or User Secrets.</summary>
public sealed class PowerTranzOptions
{
    public const string SectionName = "PowerTranz";

    public string BaseUrl { get; set; } = "https://staging.ptranz.com";

    public string PowerTranzId { get; set; } = string.Empty;

    public string PowerTranzPassword { get; set; } = string.Empty;

    /// <summary>"Auth" (hold funds, capture later) or "Sale" (authorize and capture).</summary>
    public string Transaction { get; set; } = "Auth";

    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// PowerTranz Source field names for the network token cryptogram and ECI. Empty = not sent (the token goes in
    /// CardPan like a card). Not needed for PAN payloads. Fill in from the PowerTranz API docs.
    /// </summary>
    public string CryptogramSourceField { get; set; } = string.Empty;

    public string EciSourceField { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(PowerTranzId)
        && !string.IsNullOrWhiteSpace(PowerTranzPassword);
}
