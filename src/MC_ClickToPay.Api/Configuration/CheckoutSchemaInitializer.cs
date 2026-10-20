using MC_ClickToPay.Services.Database;
using MySqlConnector;

namespace MC_ClickToPay.Api.Configuration;

/// <summary>
/// Applies the pending database migrations (MC_ClickToPay.Services/Database/Migrations) when the API starts, so each
/// deployment brings its database up to date before serving requests.
/// <list type="bullet">
/// <item>Database unreachable: logs an error and keeps starting; checkout state is simply not recorded until it is
/// reachable (the API never fails a request because of the database). Restart the API to migrate then.</item>
/// <item>A migration fails, or an applied one was modified: the API does not start, because the schema cannot be
/// trusted.</item>
/// </list>
/// </summary>
public sealed class CheckoutSchemaInitializer(DatabaseMigrator migrator, ILogger<CheckoutSchemaInitializer> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await migrator.MigrateAsync(cancellationToken);
        }
        catch (DatabaseMigrationException ex)
        {
            logger.LogCritical(ex, "Database migration failed; the API is not started: {Message}", ex.Message);
            throw;
        }
        catch (Exception ex) when (ex is MySqlException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException)
        {
            logger.LogError(ex,
                "Checkout database is not reachable, so its migrations were not applied: checkouts and confirmations will not be recorded until it is (restart the API then).");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
