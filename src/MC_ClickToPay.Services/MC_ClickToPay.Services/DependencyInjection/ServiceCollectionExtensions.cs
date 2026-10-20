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
    /// Registers the server-side Click to Pay client (/checkout and /checkout/confirmations), the daily
    /// transaction file and the confirmation batch that processes it. Requires
    /// <see cref="AddMastercardPayloadDecryption{TKeyProvider}"/>; the API configures <see cref="MastercardCheckoutOptions"/>.
    /// </summary>
    public static IServiceCollection AddMastercardCheckout<TSigningKeyProvider>(this IServiceCollection services)
        where TSigningKeyProvider : class, IMastercardSigningKeyProvider
    {
        services.AddScoped<IMastercardSigningKeyProvider, TSigningKeyProvider>();
        services.AddHttpClient<IMastercardCheckoutService, MastercardCheckoutService>(
            client => client.Timeout = TimeSpan.FromSeconds(30));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICheckoutTransactionFile, CsvCheckoutTransactionFile>();
        services.TryAddScoped<CheckoutConfirmationBatch>();
        services.TryAddSingleton<ICheckoutTransactionStore, NoCheckoutTransactionStore>();
        services.TryAddScoped<CheckoutStateRecorder>();
        return services;
    }

    /// <summary>
    /// Records checkout state in MySQL (table checkout_transactions; the API creates it at startup with
    /// <see cref="MySqlCheckoutTransactionStore.EnsureSchemaAsync"/>). Without a connection string nothing is recorded
    /// and the API works as before. Call it before <see cref="AddMastercardCheckout{T}"/>.
    /// </summary>
    public static IServiceCollection AddCheckoutDatabase(this IServiceCollection services, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return services;
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(_ => new MySqlConnector.MySqlDataSource(connectionString));
        services.AddSingleton<MySqlCheckoutTransactionStore>();
        services.AddSingleton<ICheckoutTransactionStore>(sp => sp.GetRequiredService<MySqlCheckoutTransactionStore>());
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
