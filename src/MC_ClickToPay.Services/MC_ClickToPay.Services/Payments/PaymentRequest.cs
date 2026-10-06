namespace MC_ClickToPay.Services.Payments;

/// <summary>
/// Processor-neutral payment built from a decrypted Click to Pay payload. The fields follow the PowerTranz Sale
/// mapping used in demo/PowerTranz3DSecurePoc/Services/ClickToPayService.cs: the account number (network token or
/// PAN) goes in Source.CardPan with its expiry as YYMM and no CVV; the cryptogram and ECI travel separately.
/// A class, not a record, so ToString() never prints card data.
/// </summary>
public sealed class PaymentRequest
{
    public required string OrderId { get; init; }

    public required decimal Amount { get; init; }

    /// <summary>ISO 4217 code as received (alphabetic "USD" or numeric "840").</summary>
    public required string CurrencyCode { get; init; }

    public required PaymentCredentialType CredentialType { get; init; }

    /// <summary>token.paymentToken (network token) or card.primaryAccountNumber (PAN).</summary>
    public required string AccountNumber { get; init; }

    /// <summary>YYMM, the format PowerTranz expects in Source.CardExpiration.</summary>
    public required string Expiration { get; init; }

    /// <summary>
    /// DSRP cryptogram (dynamicData.dynamicDataValue with CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM): always with a
    /// network token; also with a PAN in the "DSRP + PAN" payload.
    /// </summary>
    public string? Cryptogram { get; init; }

    /// <summary>
    /// Dynamic card security code (DTVC, dynamicData.dynamicDataValue with DYNAMIC_CARD_SECURITY_CODE): used in place
    /// of the CVC2 with a PAN. Never logged or returned.
    /// </summary>
    public string? SecurityCode { get; init; }

    /// <summary>
    /// dynamicData.dynamicDataType: CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM, DYNAMIC_CARD_SECURITY_CODE or NONE.
    /// </summary>
    public string? CryptogramType { get; init; }

    /// <summary>assuranceData.eci from Mastercard /checkout (not inside the encrypted payload), when the caller has it.</summary>
    public string? Eci { get; init; }

    public string? PaymentAccountReference { get; init; }

    public string? CardholderName { get; init; }

    public PaymentBillingAddress? BillingAddress { get; init; }

    /// <summary>The only account digits safe to return or log.</summary>
    public string Last4 => AccountNumber[^4..];
}
