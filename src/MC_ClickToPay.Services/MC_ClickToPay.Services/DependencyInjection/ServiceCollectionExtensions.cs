using MC_ClickToPay.Services.Abstractions;
using MC_ClickToPay.Services.Checkout;
using MC_ClickToPay.Services.Payments;
using MC_ClickToPay.Services.Payments.PowerTranz;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MC_ClickToPay.Services.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>The consuming API supplies the implementation that resolves its private key.</summary>
    public static IServiceCollection AddMastercardPayloadDecryption<TKeyProvider>(
        this IServiceCollection services)
        where TKeyProvider : class, IPayloadDecryptionKeyProvider
    {
        services.AddScoped<IPayloadDecryptionKeyProvider, TKeyProvider>();
        services.AddScoped<IPayloadDecryptionService, PayloadDecryptionService>();
        return services;
    }

    /// <summary>
    /// Registers the server-side Click to Pay client (/checkout and /checkout/confirmations). Requires
    /// <see cref="AddMastercardPayloadDecryption{TKeyProvider}"/>; the API configures <see cref="MastercardCheckoutOptions"/>.
    /// </summary>
    public static IServiceCollection AddMastercardCheckout<TSigningKeyProvider>(this IServiceCollection services)
        where TSigningKeyProvider : class, IMastercardSigningKeyProvider
    {
        services.AddScoped<IMastercardSigningKeyProvider, TSigningKeyProvider>();
        services.AddHttpClient<IMastercardCheckoutService, MastercardCheckoutService>(
            client => client.Timeout = TimeSpan.FromSeconds(30));
        return services;
    }

    /// <summary>
    /// Registers encrypted payload -> payment confirmation. Requires <see cref="AddMastercardPayloadDecryption{TKeyProvider}"/>;
    /// the API binds <see cref="PaymentSimulationOptions"/>. The simulated processor is only a default: register a real
    /// <see cref="IPaymentProcessor"/> (e.g. PowerTranz) before or after this call to replace it.
    /// </summary>
    public static IServiceCollection AddPaymentConfirmation(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<PaymentRequestFactory>();
        services.TryAddScoped<IPaymentProcessor, SimulatedPaymentProcessor>();
        services.TryAddScoped<PaymentConfirmationService>();
        return services;
    }

    /// <summary>
    /// Makes PowerTranz the <see cref="IPaymentProcessor"/> (server-to-server Auth/Sale, no 3-D Secure). Call it before
    /// <see cref="AddPaymentConfirmation"/>; the API binds <see cref="PowerTranzOptions"/>.
    /// </summary>
    public static IServiceCollection AddPowerTranzPaymentProcessor(this IServiceCollection services, int timeoutSeconds = 60)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient<PowerTranzPaymentProcessor>(client => client.Timeout = TimeSpan.FromSeconds(timeoutSeconds));
        services.AddScoped<IPaymentProcessor>(sp => sp.GetRequiredService<PowerTranzPaymentProcessor>());
        return services;
    }
}
