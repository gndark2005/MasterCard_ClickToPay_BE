namespace PowerTranz3DSecurePoc.Models;

/// <summary>
/// Response of /Api/spi/Sale. The same shape is posted (as the "Response" form field)
/// to MerchantResponseUrl when the 3DS flow ends and returned by /Api/spi/Payment, so it is reused for all three.
/// </summary>
public sealed class SaleResponse
{
    public int TransactionType { get; set; }
    public bool Approved { get; set; }
    public string? TransactionIdentifier { get; set; }
    public decimal TotalAmount { get; set; }
    public string? CurrencyCode { get; set; }
    public string? CardBrand { get; set; }
    public string? IsoResponseCode { get; set; }
    public string? ResponseMessage { get; set; }
    public string? OrderIdentifier { get; set; }
    public string? RedirectData { get; set; }
    public string? SpiToken { get; set; }
    public string? AuthorizationCode { get; set; }
    public string? RRN { get; set; }
    public RiskManagement? RiskManagement { get; set; }
    public List<PowerTranzError>? Errors { get; set; }

    public bool RequiresThreeDSecure =>
        string.Equals(IsoResponseCode, "SP4", StringComparison.OrdinalIgnoreCase);
}

public sealed class RiskManagement
{
    public ThreeDSecureResult? ThreeDSecure { get; set; }
}

public sealed class ThreeDSecureResult
{
    public string? Eci { get; set; }
    public string? AuthenticationStatus { get; set; }
    public string? ProtocolVersion { get; set; }
    public string? DsTransId { get; set; }
    /// <summary>Issuer message; PowerTranz requires showing it to the cardholder when present.</summary>
    public string? CardholderInfo { get; set; }
    // Cavv/Xid are intentionally not mapped: authentication values should not be logged.
}

public sealed class PowerTranzError
{
    public string? Code { get; set; }
    public string? Message { get; set; }
}
