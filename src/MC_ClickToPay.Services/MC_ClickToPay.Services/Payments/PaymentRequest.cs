namespace MC_ClickToPay.Services.Payments;

/// <summary>
/// Processor-neutral payment built from a decrypted Click to Pay payload. The fields follow the PowerTranz Sale
/// mapping used in demo/PowerTranz3DSecurePoc/Services/ClickToPayService.cs: the network token goes in
/// Source.CardPan with its expiry as YYMM and no CVV; the cryptogram and ECI travel separately.
/// A class, not a record, so ToString() never prints card data.
/// </summary>
public sealed class PaymentRequest
{
    public required string OrderId { get; init; }

    public required decimal Amount { get; init; }

    /// <summary>ISO 4217 code as received (alphabetic "USD" or numeric "840").</summary>
    public required string CurrencyCode { get; init; }

    /// <summary>Network token (DPAN) from token.paymentToken.</summary>
    public required string NetworkToken { get; init; }

    /// <summary>YYMM, the format PowerTranz expects in Source.CardExpiration.</summary>
    public required string TokenExpiration { get; init; }

    /// <summary>dynamicData.dynamicDataValue (the token cryptogram).</summary>
    public required string Cryptogram { get; init; }

    public required string CryptogramType { get; init; }

    /// <summary>assuranceData.eci from Mastercard /checkout (not inside the encrypted payload), when the caller has it.</summary>
    public string? Eci { get; init; }

    public string? PaymentAccountReference { get; init; }

    public string? CardholderName { get; init; }

    public PaymentBillingAddress? BillingAddress { get; init; }

    /// <summary>The only token digits safe to return or log.</summary>
    public string TokenLast4 => NetworkToken[^4..];
}
