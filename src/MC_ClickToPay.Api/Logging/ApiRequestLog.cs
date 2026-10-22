using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using MC_ClickToPay.Services.Database;

namespace MC_ClickToPay.Api.Logging;

/// <summary>Values an endpoint adds to its request's log row.</summary>
public static class ApiRequestLog
{
    private const string CorrelationIdKey = "ApiRequestLog.SrcCorrelationId";

    /// <summary>The checkout's srcCorrelationId, to find every request of one checkout.</summary>
    public static void SetSrcCorrelationId(HttpContext context, string? srcCorrelationId)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Items[CorrelationIdKey] = srcCorrelationId;
    }

    internal static string? GetSrcCorrelationId(HttpContext context) => context.Items[CorrelationIdKey] as string;
}

/// <summary>
/// Requests waiting to be written to api_request_log. Bounded: when the database is slow or down and it is full, new
/// entries are dropped (the file log still has them) rather than holding memory or requests.
/// </summary>
public sealed class ApiRequestLogQueue
{
    public const int Capacity = 10_000;

    private readonly Channel<ApiRequestLogEntry> channel = Channel.CreateBounded<ApiRequestLogEntry>(
        new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public bool TryEnqueue(ApiRequestLogEntry entry) => channel.Writer.TryWrite(entry);

    internal ChannelReader<ApiRequestLogEntry> Reader => channel.Reader;
}

/// <summary>
/// Writes the queued requests to the database in batches. A database error is logged (file log) and the batch dropped:
/// the request log never fails or delays a request.
/// </summary>
public sealed class ApiRequestLogWriter(ApiRequestLogQueue queue, IApiRequestLogStore store, ILogger<ApiRequestLogWriter> logger)
    : BackgroundService
{
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<ApiRequestLogEntry>(BatchSize);
        try
        {
            while (await queue.Reader.WaitToReadAsync(stoppingToken))
            {
                while (batch.Count < BatchSize && queue.Reader.TryRead(out var entry))
                {
                    batch.Add(entry);
                }

                await WriteAsync(batch, stoppingToken);
                batch.Clear();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: write what is already queued.
            while (queue.Reader.TryRead(out var entry))
            {
                batch.Add(entry);
            }

            await WriteAsync(batch, CancellationToken.None);
        }
    }

    private async Task WriteAsync(List<ApiRequestLogEntry> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            await store.WriteAsync(batch, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "{Count} request log rows were not written to the database (they are in the file log).", batch.Count);
        }
    }
}

/// <summary>
/// Records every request to /api/* in the file log and queues it for api_request_log: method, route template, status,
/// duration, correlation ids and, for errors, the Problem Details title/detail or the exception type. Bodies are never
/// stored (they hold card data and SPI tokens); only the first bytes of an error response are read, and only to take
/// its title and detail.
/// </summary>
public sealed class ApiRequestLogMiddleware(RequestDelegate next, ApiRequestLogQueue queue, TimeProvider time,
    ILogger<ApiRequestLogMiddleware> logger)
{
    private const int CapturedBytes = 8 * 1024;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) ||
            HttpMethods.IsOptions(context.Request.Method))
        {
            await next(context);
            return;
        }

        var requestAt = time.GetUtcNow();
        var started = time.GetTimestamp();
        var body = context.Response.Body;
        // Only error responses are copied: a 200 of /api/checkout carries card data and is never kept.
        await using var capture = new CapturingStream(body, CapturedBytes, () => context.Response.StatusCode >= 400);
        context.Response.Body = capture;
        string? exceptionType = null;
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            exceptionType = ex.GetType().FullName;
            throw;
        }
        finally
        {
            context.Response.Body = body;
            Record(context, requestAt, (int)time.GetElapsedTime(started).TotalMilliseconds, capture, exceptionType);
        }
    }

    private void Record(HttpContext context, DateTimeOffset requestAt, int durationMs, CapturingStream capture,
        string? exceptionType)
    {
        try
        {
            // An unhandled exception becomes a 500 later in the pipeline.
            var status = exceptionType is null ? context.Response.StatusCode : StatusCodes.Status500InternalServerError;
            var (title, detail) = status >= 400 ? ReadProblem(capture.Captured) : (null, null);
            var entry = new ApiRequestLogEntry
            {
                RequestAt = requestAt,
                Method = context.Request.Method,
                Route = RouteOf(context),
                HttpStatus = status,
                DurationMs = durationMs,
                SrcCorrelationId = ApiRequestLog.GetSrcCorrelationId(context),
                XCorrelationId = context.Response.Headers["X-Correlation-Id"].FirstOrDefault(),
                ErrorTitle = title,
                ErrorDetail = detail,
                ExceptionType = exceptionType,
                ClientIp = ClientIp(context),
                TraceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier
            };

            var level = status >= 500 ? LogLevel.Error : status >= 400 ? LogLevel.Warning : LogLevel.Information;
            logger.Log(level,
                "{Method} {Route} -> {Status} in {DurationMs} ms (srcCorrelationId {SrcCorrelationId}, x-correlation-id {XCorrelationId}){Error}",
                entry.Method, entry.Route, entry.HttpStatus, entry.DurationMs, entry.SrcCorrelationId ?? "-",
                entry.XCorrelationId ?? "-", title is null && detail is null ? "" : $": {title} {detail}".TrimEnd());

            if (!queue.TryEnqueue(entry))
            {
                logger.LogWarning("Request log queue is full; {Method} {Route} was not queued for the database.", entry.Method, entry.Route);
            }
        }
        catch (Exception ex)
        {
            // The log must never break the response.
            logger.LogError(ex, "The request could not be logged.");
        }
    }

    // The template, never the raw path: /api/confirmations/{spiToken} would otherwise hold the SPI token.
    private static string RouteOf(HttpContext context)
    {
        if (context.GetEndpoint() is RouteEndpoint { RoutePattern.RawText: { } template })
        {
            return template.StartsWith('/') ? template : "/" + template;
        }

        var path = context.Request.Path.Value ?? "/";
        return path.StartsWith("/api/confirmations/", StringComparison.OrdinalIgnoreCase)
            ? "/api/confirmations/{spiToken}"
            : path;
    }

    // Behind Cloudflare the client is in CF-Connecting-IP; informational only (the header can be forged).
    private static string? ClientIp(HttpContext context) =>
        context.Request.Headers["CF-Connecting-IP"].FirstOrDefault()
        ?? context.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
        ?? context.Connection.RemoteIpAddress?.ToString();

    // Problem Details (title, detail) or FastEndpoints validation errors (message, errors).
    private static (string? Title, string? Detail) ReadProblem(byte[] body)
    {
        if (body.Length == 0)
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var title = String(root, "title") ?? String(root, "message");
            var detail = String(root, "detail");
            if (detail is null && root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                detail = string.Join(" ", errors.EnumerateObject().SelectMany(e =>
                    e.Value.ValueKind == JsonValueKind.Array
                        ? e.Value.EnumerateArray().Select(m => $"{e.Name}: {m.GetString()}")
                        : [$"{e.Name}: {e.Value}"]));
            }

            return (title, detail);
        }
        catch (JsonException)
        {
            return (null, $"Non-JSON error response ({body.Length} bytes).");
        }
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Passes every write through and, while <paramref name="keep"/> is true, keeps a copy of the first bytes.</summary>
    private sealed class CapturingStream(Stream inner, int limit, Func<bool> keep) : Stream
    {
        private readonly MemoryStream copy = new();

        public byte[] Captured => copy.ToArray();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Keep(buffer);
            inner.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Keep(buffer.Span);
            return inner.WriteAsync(buffer, cancellationToken);
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                copy.Dispose();
            }

            base.Dispose(disposing);
        }

        private void Keep(ReadOnlySpan<byte> buffer)
        {
            var room = limit - (int)copy.Length;
            if (room > 0 && keep())
            {
                copy.Write(buffer[..Math.Min(room, buffer.Length)]);
            }
        }
    }
}
