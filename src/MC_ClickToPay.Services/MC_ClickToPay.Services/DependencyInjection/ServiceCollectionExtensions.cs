using MC_ClickToPay.Services.Abstractions;
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
}
