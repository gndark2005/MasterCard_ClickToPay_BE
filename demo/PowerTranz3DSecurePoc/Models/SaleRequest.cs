namespace PowerTranz3DSecurePoc.Models;

// Serialized with PascalCase property names, which is what PowerTranz expects.
public sealed class SaleRequest
{
    public string TransactionIdentifier { get; set; } = "";
    public decimal TotalAmount { get; set; }
    public string CurrencyCode { get; set; } = "";
    public bool ThreeDSecure { get; set; } = true;
    public string OrderIdentifier { get; set; } = "";
    public bool AddressMatch { get; set; }
    /// <summary>Only card-not-present flags when the card is entered in the Hosted Payment Page.</summary>
    public SaleSource Source { get; set; } = new();
    public SaleBillingAddress? BillingAddress { get; set; }
    public SaleExtendedData ExtendedData { get; set; } = new();
}

// Nullable so unused card fields are omitted (nulls are not serialized).
public sealed class SaleSource
{
    public string? CardPan { get; set; }
    public string? CardCvv { get; set; }
    public string? CardExpiration { get; set; }
    public string? CardholderName { get; set; }
    // Card-not-present flags, sent (all false) on Hosted Payment Page requests as in the PowerTranz staging example.
    public bool? CardPresent { get; set; }
    public bool? CardEmvFallback { get; set; }
    public bool? ManualEntry { get; set; }
    public bool? Debit { get; set; }
    public bool? Contactless { get; set; }
    /// <summary>Click to Pay cryptogram/ECI under the configured PowerTranz field names (see ClickToPayOptions).</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, object>? ExtraFields { get; set; }
}

public sealed class SaleBillingAddress
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Line1 { get; set; }
    public string? Line2 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public string? CountryCode { get; set; }
    public string? EmailAddress { get; set; }
    public string? PhoneNumber { get; set; }
}

public sealed class SaleExtendedData
{
    public SaleThreeDSecure ThreeDSecure { get; set; } = new();
    public SaleHostedPage? HostedPage { get; set; }
    public string MerchantResponseUrl { get; set; } = "";
}

public sealed class SaleHostedPage
{
    public string PageSet { get; set; } = "";
    public string PageName { get; set; } = "";
}

public sealed class SaleThreeDSecure
{
    /// <summary>4 = 600x400 (default used by PowerTranz samples); 5 = full page.</summary>
    public int ChallengeWindowSize { get; set; } = 4;
    /// <summary>"01" = no preference; "03" = challenge requested (merchant preference).</summary>
    public string ChallengeIndicator { get; set; } = "01";
}
