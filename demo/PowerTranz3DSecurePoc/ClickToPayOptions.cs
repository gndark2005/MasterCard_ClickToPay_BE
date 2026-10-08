namespace PowerTranz3DSecurePoc;

/// <summary>
/// Bound from the "ClickToPay" section. Enables the "Click to Pay" tab on the checkout page: Mastercard Unified
/// Checkout runs in the iframe, and the MasterCard_ClickToPay_BE API completes the checkout (Mastercard /checkout +
/// payload decryption) before the usual PowerTranz Auth/Sale → 3DS → Payment flow.
/// </summary>
public sealed class ClickToPayOptions
{
    public const string SectionName = "ClickToPay";

    public bool Enabled { get; set; }

    /// <summary>MasterCard_ClickToPay_BE base URL, e.g. https://localhost:7180.</summary>
    public string ApiBaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    /// <summary>
    /// Accepts the untrusted ASP.NET Core development certificate, only when ApiBaseUrl points to localhost.
    /// Not needed after "dotnet dev-certs https --trust".
    /// </summary>
    public bool AllowUntrustedLocalhostCertificate { get; set; }

    public string SrcDpaId { get; set; } = "";
    public string DpaName { get; set; } = "";
    /// <summary>Unified Checkout SDK script (sandbox by default).</summary>
    public string SdkUrl { get; set; } = "https://sandbox.src.mastercard.com/srci/integration/2/lib.js";
    public List<string> CardBrands { get; set; } = ["mastercard", "maestro", "visa", "amex", "discover"];
    /// <summary>Email pre-filled on the Click to Pay sign-in step (sandbox consumer).</summary>
    public string DefaultEmail { get; set; } = "";

    /// <summary>
    /// ISO 4217 alphabetic currency for Mastercard (e.g. "EUR"). Empty = derived from PowerTranz:CurrencyCode.
    /// </summary>
    public string TransactionCurrencyCode { get; set; } = "";

    /// <summary>
    /// PowerTranz Source field names for the Click to Pay cryptogram and ECI. Leave empty until confirmed with the
    /// PowerTranz SPI documentation: empty names are not sent (the network token is sent as CardPan).
    /// </summary>
    public string CryptogramSourceField { get; set; } = "";
    public string EciSourceField { get; set; } = "";

    /// <summary>
    /// Testing aid: prints the POST /api/checkout body (ready for Swagger) and stops there, without calling the API
    /// or PowerTranz, so the single-use Mastercard identifiers are still unused when pasted in Swagger.
    /// </summary>
    public bool SwaggerHandOff { get; set; }

    public IEnumerable<string> GetMissingSettings()
    {
        if (!Enabled) yield break;
        if (!Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out _)) yield return "ClickToPay:ApiBaseUrl (absolute URL)";
        if (string.IsNullOrWhiteSpace(ApiKey)) yield return "ClickToPay:ApiKey";
        if (string.IsNullOrWhiteSpace(SrcDpaId)) yield return "ClickToPay:SrcDpaId";
        if (string.IsNullOrWhiteSpace(DpaName)) yield return "ClickToPay:DpaName";
    }

    /// <summary>Mastercard expects alphabetic codes; PowerTranz uses numeric ones.</summary>
    public string ResolveCurrency(string powerTranzNumericCode)
    {
        if (!string.IsNullOrWhiteSpace(TransactionCurrencyCode)) return TransactionCurrencyCode.ToUpperInvariant();
        return powerTranzNumericCode switch
        {
            "840" => "USD",
            "978" => "EUR",
            "388" => "JMD",
            "780" => "TTD",
            "052" or "52" => "BBD",
            "044" or "44" => "BSD",
            "136" => "KYD",
            "826" => "GBP",
            "124" => "CAD",
            _ => throw new InvalidOperationException(
                $"Set ClickToPay:TransactionCurrencyCode: no alphabetic code known for PowerTranz currency {powerTranzNumericCode}."),
        };
    }
}
