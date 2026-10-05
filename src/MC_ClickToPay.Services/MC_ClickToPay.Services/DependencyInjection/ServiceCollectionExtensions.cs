using MC_ClickToPay.Services.Abstractions;
using MC_ClickToPay.Services.Checkout;
using Microsoft.Extensions.DependencyInjection;

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
}
