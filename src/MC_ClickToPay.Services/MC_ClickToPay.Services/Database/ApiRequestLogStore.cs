using MySqlConnector;

namespace MC_ClickToPay.Services.Database;

/// <summary>
/// One request to /api/*, as stored in api_request_log (migration 0002). Never holds bodies, card data or SPI tokens:
/// <see cref="Route"/> is the route template.
/// </summary>
public sealed record ApiRequestLogEntry
{
    public required DateTimeOffset RequestAt { get; init; }

    public required string Method { get; init; }

    public required string Route { get; init; }

    public required int HttpStatus { get; init; }

    public required int DurationMs { get; init; }

    public string? SrcCorrelationId { get; init; }

    public string? XCorrelationId { get; init; }

    public string? ErrorTitle { get; init; }

    public string? ErrorDetail { get; init; }

    public string? ExceptionType { get; init; }

    public string? ClientIp { get; init; }

    public string? TraceId { get; init; }
}

public interface IApiRequestLogStore
{
    Task WriteAsync(IReadOnlyList<ApiRequestLogEntry> entries, CancellationToken cancellationToken = default);
}

/// <summary>Used when ConnectionStrings:ClickToPay is not set: requests are only in the file log.</summary>
public sealed class NoApiRequestLogStore : IApiRequestLogStore
{
    public Task WriteAsync(IReadOnlyList<ApiRequestLogEntry> entries, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

public sealed class MySqlApiRequestLogStore(MySqlDataSource dataSource) : IApiRequestLogStore
{
    private const string InsertSql = """
        INSERT INTO api_request_log
            (request_at, method, route, http_status, duration_ms, src_correlation_id, x_correlation_id,
             error_title, error_detail, exception_type, client_ip, trace_id)
        VALUES
            (@requestAt, @method, @route, @status, @durationMs, @srcCorrelationId, @xCorrelationId,
             @errorTitle, @errorDetail, @exceptionType, @clientIp, @traceId)
        """;

    public async Task WriteAsync(IReadOnlyList<ApiRequestLogEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var entry in entries)
        {
            await using var command = new MySqlCommand(InsertSql, connection, transaction);
            command.Parameters.AddWithValue("@requestAt", entry.RequestAt.UtcDateTime);
            command.Parameters.AddWithValue("@method", Cut(entry.Method, 10));
            command.Parameters.AddWithValue("@route", Cut(entry.Route, 200));
            command.Parameters.AddWithValue("@status", entry.HttpStatus);
            command.Parameters.AddWithValue("@durationMs", entry.DurationMs);
            command.Parameters.AddWithValue("@srcCorrelationId", Cut(entry.SrcCorrelationId, 256));
            command.Parameters.AddWithValue("@xCorrelationId", Cut(entry.XCorrelationId, 256));
            command.Parameters.AddWithValue("@errorTitle", Cut(entry.ErrorTitle, 255));
            command.Parameters.AddWithValue("@errorDetail", Cut(entry.ErrorDetail, 2000));
            command.Parameters.AddWithValue("@exceptionType", Cut(entry.ExceptionType, 255));
            command.Parameters.AddWithValue("@clientIp", Cut(entry.ClientIp, 64));
            command.Parameters.AddWithValue("@traceId", Cut(entry.TraceId, 64));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    // Blank strings are stored as NULL; long ones are cut to the column size.
    private static object Cut(string? value, int length) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Length > length ? value[..length] : value;
}
