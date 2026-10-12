namespace MC_ClickToPay.Services.Checkout;

/// <summary>
/// Mastercard Click to Pay server-side API (POST /srci/api/checkout and /checkout/confirmations). The credentials are
/// the integrator's and shared by every merchant; the merchant's srcDpaId comes in each request.
/// </summary>
public sealed class MastercardCheckoutOptions
{
    public string BaseUrl { get; set; } = "https://sandbox.api.mastercard.com";

    /// <summary>OAuth 1.0a consumer key from the Mastercard Developers project ("clientId!keyId").</summary>
    public string ConsumerKey { get; set; } = string.Empty;

    /// <summary>The x-openapi-clientid header: the consumer key part before "!".</summary>
    public string ClientId => ConsumerKey.Split('!')[0];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ConsumerKey);
}
