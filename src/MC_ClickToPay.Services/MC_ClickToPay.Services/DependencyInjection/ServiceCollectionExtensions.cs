using MC_ClickToPay.Services.Abstractions;
using MC_ClickToPay.Services.Checkout;
using MC_ClickToPay.Services.Confirmations;
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
    /// transaction file and the confirmation of its transactions by SPI token. Requires
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
        services.TryAddScoped<TransactionConfirmationService>();
        services.TryAddSingleton<ICheckoutTransactionStore, NoCheckoutTransactionStore>();
        services.TryAddScoped<CheckoutStateRecorder>();
        return services;
    }

    /// <summary>
    /// Records checkout state (table checkout_transactions) and the API request log (table api_request_log) in MySQL.
    /// The schema comes from the migrations in Database/Migrations, applied at startup by
    /// <see cref="Database.DatabaseMigrator"/>. Without a connection string nothing is recorded in a database and the
    /// API works as before. Call it before <see cref="AddMastercardCheckout{T}"/>.
    /// </summary>
    public static IServiceCollection AddCheckoutDatabase(this IServiceCollection services, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            services.TryAddSingleton<Database.IApiRequestLogStore, Database.NoApiRequestLogStore>();
            return services;
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(_ => new MySqlConnector.MySqlDataSource(connectionString));
        services.AddSingleton<MySqlCheckoutTransactionStore>();
        services.AddSingleton<ICheckoutTransactionStore>(sp => sp.GetRequiredService<MySqlCheckoutTransactionStore>());
        services.AddSingleton<Database.DatabaseMigrator>();
        services.TryAddSingleton<Database.IApiRequestLogStore, Database.MySqlApiRequestLogStore>();
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
