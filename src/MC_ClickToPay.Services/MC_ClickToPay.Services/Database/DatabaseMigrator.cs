using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace MC_ClickToPay.Services.Database;

/// <summary>A versioned SQL script: Database/Migrations/NNNN_description.sql, embedded in this assembly.</summary>
public sealed record DatabaseMigration(int Version, string Name, string Sql)
{
    /// <summary>SHA-256 of the script, stored when applied: an applied script must never change.</summary>
    public string Checksum { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(Sql)))).ToLowerInvariant();

    // Line endings depend on the git checkout (CRLF on Windows, LF elsewhere); they must not change the checksum.
    private static string Normalize(string sql) => sql.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
}

/// <summary>A migration cannot be applied, or an applied one was modified: the schema cannot be trusted.</summary>
public sealed class DatabaseMigrationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Applies the pending migrations in version order and records each one in schema_migrations, so every script runs
/// once per database. Runs at API startup (deployments included). A MySQL named lock lets only one instance migrate at
/// a time. MySQL commits DDL implicitly, so a script that fails halfway is not rolled back: keep each script small and
/// re-runnable (IF NOT EXISTS / IF EXISTS).
/// </summary>
public sealed partial class DatabaseMigrator(MySqlDataSource dataSource, TimeProvider time, ILogger<DatabaseMigrator> logger)
{
    public const string HistoryTable = "schema_migrations";

    private const string LockName = "clicktopay_schema_migrations";
    private const int LockTimeoutSeconds = 60;

    private const string CreateHistorySql = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            version      INT          NOT NULL PRIMARY KEY,
            name         VARCHAR(255) NOT NULL,
            checksum     CHAR(64)     NOT NULL COMMENT 'SHA-256 of the script',
            applied_at   DATETIME(3)  NOT NULL COMMENT 'UTC',
            execution_ms INT          NOT NULL
        ) ENGINE = InnoDB
        """;

    /// <summary>The migrations embedded in this assembly, in version order.</summary>
    public static IReadOnlyList<DatabaseMigration> Embedded { get; } = Load(typeof(DatabaseMigrator).Assembly);

    /// <summary>Applies the pending migrations; returns the ones applied now (empty when the database is up to date).</summary>
    public async Task<IReadOnlyList<DatabaseMigration>> MigrateAsync(CancellationToken cancellationToken = default) =>
        await MigrateAsync(Embedded, cancellationToken);

    public async Task<IReadOnlyList<DatabaseMigration>> MigrateAsync(
        IReadOnlyList<DatabaseMigration> migrations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        if (!await AcquireLockAsync(connection, cancellationToken))
        {
            throw new DatabaseMigrationException(
                $"Another instance kept the migration lock for more than {LockTimeoutSeconds}s; restart when it has finished.");
        }

        try
        {
            await ExecuteAsync(connection, CreateHistorySql, cancellationToken);
            var applied = await ReadAppliedAsync(connection, cancellationToken);

            foreach (var migration in migrations)
            {
                if (applied.TryGetValue(migration.Version, out var checksum) && checksum != migration.Checksum)
                {
                    throw new DatabaseMigrationException(
                        $"Migration {Label(migration)} was modified after it was applied. Never edit an applied migration: " +
                        "add a new one with the next version instead.");
                }
            }

            var pending = migrations.Where(m => !applied.ContainsKey(m.Version)).ToList();
            foreach (var migration in pending)
            {
                await ApplyAsync(connection, migration, cancellationToken);
            }

            if (pending.Count == 0)
            {
                logger.LogInformation("Database schema is up to date ({Count} migrations applied).", migrations.Count);
            }
            else
            {
                logger.LogInformation("Database schema migrated: applied {Applied} ({Count} migrations in total).",
                    string.Join(", ", pending.Select(Label)), migrations.Count);
            }

            return pending;
        }
        finally
        {
            await ReleaseLockAsync(connection);
        }
    }

    /// <summary>Reads NNNN_description.sql resources from Database/Migrations; versions must be unique.</summary>
    public static IReadOnlyList<DatabaseMigration> Load(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var migrations = new List<DatabaseMigration>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.Contains(".Database.Migrations.", StringComparison.Ordinal)))
        {
            var fileName = resource[(resource.IndexOf(".Database.Migrations.", StringComparison.Ordinal) + ".Database.Migrations.".Length)..];
            var match = FileNamePattern().Match(fileName);
            if (!match.Success)
            {
                throw new DatabaseMigrationException(
                    $"Migration file {fileName} must be named NNNN_description.sql (e.g. 0002_add_column.sql).");
            }

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            migrations.Add(new DatabaseMigration(
                int.Parse(match.Groups["version"].Value, CultureInfo.InvariantCulture), match.Groups["name"].Value, reader.ReadToEnd()));
        }

        var duplicate = migrations.GroupBy(m => m.Version).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new DatabaseMigrationException(
                $"Two migrations have version {duplicate.Key:D4}: {string.Join(", ", duplicate.Select(m => m.Name))}.");
        }

        return [.. migrations.OrderBy(m => m.Version)];
    }

    private async Task ApplyAsync(MySqlConnection connection, DatabaseMigration migration, CancellationToken cancellationToken)
    {
        logger.LogInformation("Applying database migration {Migration}...", Label(migration));
        var started = time.GetTimestamp();
        try
        {
            await ExecuteAsync(connection, migration.Sql, cancellationToken);
        }
        catch (MySqlException ex)
        {
            throw new DatabaseMigrationException(
                $"Migration {Label(migration)} failed: [{ex.ErrorCode}] {ex.Message} The database may be partly migrated " +
                "(MySQL commits DDL statements one by one); fix the script or the database and restart.", ex);
        }

        var elapsed = (int)time.GetElapsedTime(started).TotalMilliseconds;
        await using var record = connection.CreateCommand();
        record.CommandText = "INSERT INTO schema_migrations (version, name, checksum, applied_at, execution_ms) VALUES (@version, @name, @checksum, @appliedAt, @ms)";
        record.Parameters.AddWithValue("@version", migration.Version);
        record.Parameters.AddWithValue("@name", migration.Name);
        record.Parameters.AddWithValue("@checksum", migration.Checksum);
        record.Parameters.AddWithValue("@appliedAt", time.GetUtcNow().UtcDateTime);
        record.Parameters.AddWithValue("@ms", elapsed);
        await record.ExecuteNonQueryAsync(cancellationToken);
        logger.LogInformation("Database migration {Migration} applied in {Ms} ms.", Label(migration), elapsed);
    }

    private static async Task<Dictionary<int, string>> ReadAppliedAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        var applied = new Dictionary<int, string>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version, checksum FROM schema_migrations";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            applied[reader.GetInt32(0)] = reader.GetString(1);
        }

        return applied;
    }

    private static async Task<bool> AcquireLockAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT GET_LOCK('{LockName}', {LockTimeoutSeconds})";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task ReleaseLockAsync(MySqlConnection connection)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT RELEASE_LOCK('{LockName}')";
            await command.ExecuteScalarAsync();
        }
        catch (MySqlException)
        {
            // The lock is released anyway when the connection closes.
        }
    }

    private static async Task ExecuteAsync(MySqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 300;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Label(DatabaseMigration migration) => $"{migration.Version:D4}_{migration.Name}";

    [GeneratedRegex("^(?<version>[0-9]{4})_(?<name>[a-z0-9_]+)\\.sql$")]
    private static partial Regex FileNamePattern();
}
