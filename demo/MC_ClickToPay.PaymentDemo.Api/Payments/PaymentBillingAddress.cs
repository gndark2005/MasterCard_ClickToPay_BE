namespace MC_ClickToPay.PaymentDemo.Api.Payments;

/// <summary>Mirrors the PowerTranz Sale BillingAddress fields filled by ClickToPayService.Map in the POC.</summary>
public sealed class PaymentBillingAddress
{
    public string? FirstName { get; init; }

    public string? LastName { get; init; }

    public string? Line1 { get; init; }

    public string? Line2 { get; init; }

    public string? City { get; init; }

    public string? State { get; init; }

    public string? PostalCode { get; init; }

    /// <summary>ISO 3166 alpha-2 as sent by Mastercard; PowerTranz needs it converted to numeric.</summary>
    public string? CountryCode { get; init; }

    public string? EmailAddress { get; init; }

    public string? PhoneNumber { get; init; }
}
