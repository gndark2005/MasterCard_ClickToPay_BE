namespace PowerTranz3DSecurePoc;

/// <summary>
/// Bound from the "PowerTranz" configuration section.
/// Non-sensitive values live in appsettings.json; secrets (PowerTranzId, PowerTranzPassword, Card)
/// must come from User Secrets or environment variables.
/// </summary>
public sealed class PowerTranzOptions
{
    public const string SectionName = "PowerTranz";

    public string BaseUrl { get; set; } = "";
    public string PowerTranzId { get; set; } = "";
    public string PowerTranzPassword { get; set; } = "";
    /// <summary>Sent as PowerTranz-GatewayKey on the first SPI request when set (staging HPP example uses "Sentry").</summary>
    public string GatewayKey { get; set; } = "";
    /// <summary>First SPI request: "Auth" (hold funds; a Capture finalizes it) or "Sale" (authorize and capture).</summary>
    public string SpiTransaction { get; set; } = "Sale";

    public decimal TotalAmount { get; set; }
    public string CurrencyCode { get; set; } = "";
    public string MerchantResponseUrl { get; set; } = "";
    /// <summary>3DS challenge preference: "01" = no preference, "03" = challenge requested (always shows the challenge).</summary>
    public string ChallengeIndicator { get; set; } = "01";

    public int HttpTimeoutSeconds { get; set; } = 60;

    public CardOptions Card { get; set; } = new();
    public BillingAddressOptions? BillingAddress { get; set; }
    public BrowserOptions Browser { get; set; } = new();
    public HostedPageOptions HostedPage { get; set; } = new();

    public IEnumerable<string> GetMissingSettings()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl)) yield return "PowerTranz:BaseUrl";
        if (string.IsNullOrWhiteSpace(PowerTranzId)) yield return "PowerTranz:PowerTranzId";
        if (string.IsNullOrWhiteSpace(PowerTranzPassword)) yield return "PowerTranz:PowerTranzPassword";
        if (string.IsNullOrWhiteSpace(CurrencyCode)) yield return "PowerTranz:CurrencyCode";
        if (!Uri.TryCreate(MerchantResponseUrl, UriKind.Absolute, out _)) yield return "PowerTranz:MerchantResponseUrl (absolute URL)";
        if (SpiTransaction is not ("Auth" or "Sale")) yield return "PowerTranz:SpiTransaction (Auth or Sale)";
        // The Hosted Payment Page is optional; when set, both values are needed.
        if (!string.IsNullOrWhiteSpace(HostedPage.PageSet) || !string.IsNullOrWhiteSpace(HostedPage.PageName))
        {
            if (string.IsNullOrWhiteSpace(HostedPage.PageSet)) yield return "PowerTranz:HostedPage:PageSet";
            if (string.IsNullOrWhiteSpace(HostedPage.PageName)) yield return "PowerTranz:HostedPage:PageName";
        }
        // With a visible browser the amount comes from the cart and the card from the checkout page (or the HPP).
        if (!Browser.Headless)
            yield break;
        if (TotalAmount <= 0) yield return "PowerTranz:TotalAmount";
        if (string.IsNullOrWhiteSpace(Card.Pan)) yield return "PowerTranz:Card:Pan";
        if (string.IsNullOrWhiteSpace(Card.Expiration)) yield return "PowerTranz:Card:Expiration (YYMM)";
        if (string.IsNullOrWhiteSpace(Card.HolderName)) yield return "PowerTranz:Card:HolderName";
    }
}

public sealed class CardOptions
{
    public string Pan { get; set; } = "";
    /// <summary>Optional; omitted from the request when empty.</summary>
    public string Cvv { get; set; } = "";
    /// <summary>YYMM, as expected by PowerTranz.</summary>
    public string Expiration { get; set; } = "";
    public string HolderName { get; set; } = "";
}

public sealed class BillingAddressOptions
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Line1 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public string? CountryCode { get; set; }
    public string? EmailAddress { get; set; }
    public string? PhoneNumber { get; set; }
}

/// <summary>
/// Hosted Payment Page created in the PowerTranz Merchant Portal. When configured, the checkout page loads it in
/// the iframe for card entry; when empty, the checkout page collects the card itself.
/// </summary>
public sealed class HostedPageOptions
{
    /// <summary>
    /// The docs require a "PTZ/" prefix for pages created in the Merchant Portal; pages provided by PowerTranz
    /// (e.g. staging "GFRHPP") are used as given.
    /// </summary>
    public string PageSet { get; set; } = "";
    public string PageName { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(PageSet) && !string.IsNullOrWhiteSpace(PageName);
}

public sealed class BrowserOptions
{
    public bool Headless { get; set; }
    /// <summary>Max time to wait for the 3DS flow (including a manual challenge) to reach MerchantResponseUrl.</summary>
    public int ThreeDSecureTimeoutSeconds { get; set; } = 300;
    /// <summary>
    /// true: the POST to MerchantResponseUrl is captured and answered locally by Playwright (no merchant server needed).
    /// false: the request is captured and then forwarded to the real MerchantResponseUrl.
    /// </summary>
    public bool InterceptMerchantResponse { get; set; } = true;
    public int KeepOpenAfterCallbackSeconds { get; set; } = 5;
}
