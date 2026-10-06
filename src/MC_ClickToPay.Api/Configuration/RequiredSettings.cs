using MC_ClickToPay.Api.Keys;
using MC_ClickToPay.Services.Checkout;
using MC_ClickToPay.Services.Payments;
using MC_ClickToPay.Services.Payments.PowerTranz;

namespace MC_ClickToPay.Api.Configuration;

/// <summary>
/// Binds the API settings. Only what payload decryption needs is required and validated when the host starts,
/// so a missing value stops the API with a clear message instead of failing later with HTTP 401/503.
/// The Mastercard API settings stay optional: without them /api/checkout/* returns 502/503 but decryption works.
/// </summary>
public static class RequiredSettings
{
    public static IServiceCollection AddRequiredSettings(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ApiKeySettings>()
            .Bind(configuration.GetSection(ApiKeySettings.SectionName))
            .Validate(s => !string.IsNullOrWhiteSpace(s.ApiKey),
                "Authentication:ApiKey is required (the X-Api-Key callers must send).")
            .ValidateOnStart();

        // Private key of the Payload Encryption key pair registered with Mastercard.
        services.AddOptions<PayloadEncryptionOptions>()
            .Bind(configuration.GetSection("PayloadEncryption"))
            .Validate(o => !string.IsNullOrWhiteSpace(o.CertificatePath),
                "PayloadEncryption:CertificatePath is required: the Payload Encryption private key (.pem, or .p12/.pfx) " +
                "used to decrypt encryptedPayload.")
            .ValidateOnStart();

        // Optional until the full Mastercard → PowerTranz flow is used (/api/checkout/complete and /confirmations).
        services.Configure<MastercardCheckoutOptions>(configuration.GetSection("MastercardApi"));
        services.Configure<MastercardSigningKeyOptions>(configuration.GetSection("MastercardApi"));

        // Optional: outcome of the simulated processor behind POST /api/payments/confirm (default Approved).
        services.Configure<PaymentSimulationOptions>(configuration.GetSection("PaymentSimulation"));

        // Optional: with PowerTranzId/PowerTranzPassword set, PowerTranz replaces the simulated processor.
        services.Configure<PowerTranzOptions>(configuration.GetSection(PowerTranzOptions.SectionName));

        return services;
    }
}

public sealed class ApiKeySettings
{
    public const string SectionName = "Authentication";

    public string ApiKey { get; set; } = string.Empty;
}
