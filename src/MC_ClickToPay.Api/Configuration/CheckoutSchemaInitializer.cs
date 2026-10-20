using MC_ClickToPay.Services.Checkout;
using MySqlConnector;

namespace MC_ClickToPay.Api.Configuration;

/// <summary>
/// Creates the checkout_transactions table at startup when it does not exist. A database that is down only logs an
/// error: the API keeps working and the state is simply not recorded until it is reachable.
/// </summary>
public sealed class CheckoutSchemaInitializer(MySqlCheckoutTransactionStore store, ILogger<CheckoutSchemaInitializer> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await store.EnsureSchemaAsync(cancellationToken);
            logger.LogInformation("Checkout database ready (table checkout_transactions).");
        }
        catch (Exception ex) when (ex is MySqlException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException)
        {
            logger.LogError(ex, "Checkout database is not reachable: checkouts and confirmations will not be recorded until it is.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
