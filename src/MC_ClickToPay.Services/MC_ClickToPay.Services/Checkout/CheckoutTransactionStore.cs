using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace MC_ClickToPay.Services.Checkout;

/// <summary>State of a checkout in the checkout_transactions table.</summary>
public static class CheckoutTransactionStatus
{
    /// <summary>POST /api/checkout succeeded; not confirmed to Mastercard yet.</summary>
    public const string Pending = "Pending";

    /// <summary>Mastercard /checkout/confirmations answered 204.</summary>
    public const string Confirmed = "Confirmed";

    /// <summary>The last confirmation was rejected by Mastercard or failed; it can be confirmed again.</summary>
    public const string ConfirmationFailed = "ConfirmationFailed";
}

/// <summary>Result of one confirmation call, as recorded in the table. Never holds payload data.</summary>
public sealed record ConfirmationOutcome(bool Confirmed, string? FlowId, int? HttpStatus, string? Detail);

/// <summary>
/// The checkout state database (MySQL table checkout_transactions, see docs/db/schema.sql). It is the source of the
/// checkout state; the daily CSV file stays as a backup and for batch uploads.
/// </summary>
public interface ICheckoutTransactionStore
{
    /// <summary>Records a successful checkout as Pending; a repeated srcCorrelationId keeps its current status.</summary>
    Task RecordCheckoutAsync(CompleteCheckoutRequest request, CancellationToken cancellationToken = default);

    /// <summary>Records the result of a confirmation (Confirmed or ConfirmationFailed); a Confirmed row stays Confirmed.</summary>
    Task RecordConfirmationAsync(CheckoutConfirmationRequest request, ConfirmationOutcome outcome,
        CancellationToken cancellationToken = default);
}

public sealed class CheckoutDatabaseOptions
{
    /// <summary>ConnectionStrings:ClickToPay. Empty = no database (the API works without it).</summary>
    public string ConnectionString { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString);
}

/// <summary>Used when ConnectionStrings:ClickToPay is not set: nothing is recorded.</summary>
public sealed class NoCheckoutTransactionStore : ICheckoutTransactionStore
{
    public Task RecordCheckoutAsync(CompleteCheckoutRequest request, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task RecordConfirmationAsync(CheckoutConfirmationRequest request, ConfirmationOutcome outcome,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class MySqlCheckoutTransactionStore(MySqlDataSource dataSource, TimeProvider time) : ICheckoutTransactionStore
{
    /// <summary>The table of docs/db/schema.sql, created at startup when it does not exist.</summary>
    public const string CreateTableSql = """
        CREATE TABLE IF NOT EXISTS checkout_transactions (
            id                             BIGINT AUTO_INCREMENT PRIMARY KEY,
            src_correlation_id             VARCHAR(256)  NOT NULL,
            merchant_transaction_id        VARCHAR(255)  NULL,
            flow_id                        VARCHAR(256)  NULL,
            x_correlation_id               VARCHAR(256)  NULL,
            src_dpa_id                     VARCHAR(256)  NULL,
            spi_token                      VARCHAR(2048) NULL,
            transaction_amount             DECIMAL(14,2) NULL,
            transaction_currency           CHAR(3)       NULL,
            status                         VARCHAR(20)   NOT NULL,
            checkout_at                    DATETIME(3)   NULL     COMMENT 'UTC',
            confirmation_attempts          INT           NOT NULL DEFAULT 0,
            last_confirmation_at           DATETIME(3)   NULL     COMMENT 'UTC',
            confirmed_at                   DATETIME(3)   NULL     COMMENT 'UTC',
            checkout_event_type            CHAR(2)       NULL,
            checkout_event_status          CHAR(2)       NULL,
            confirmation_status            CHAR(2)       NULL,
            confirmation_reason            VARCHAR(255)  NULL,
            network_authorization_code     VARCHAR(64)   NULL,
            network_transaction_identifier VARCHAR(255)  NULL,
            mastercard_http_status         INT           NULL     COMMENT 'Mastercard status of the last failed confirmation',
            mastercard_detail              VARCHAR(1000) NULL     COMMENT 'Reason code and description; never payload data',
            mastercard_flow_id             VARCHAR(256)  NULL     COMMENT 'X-Src-Cx-Flow-Id returned by the last confirmation',
            created_at                     DATETIME(3)   NOT NULL COMMENT 'UTC',
            updated_at                     DATETIME(3)   NOT NULL COMMENT 'UTC',
            UNIQUE KEY ux_checkout_src_correlation_id (src_correlation_id),
            KEY ix_checkout_status (status),
            KEY ix_checkout_checkout_at (checkout_at)
        ) ENGINE = InnoDB
        """;

    // A repeated checkout (same srcCorrelationId) refreshes its data but keeps its status and confirmation history.
    private const string RecordCheckoutSql = """
        INSERT INTO checkout_transactions
            (src_correlation_id, merchant_transaction_id, flow_id, x_correlation_id, src_dpa_id, spi_token,
             transaction_amount, transaction_currency, status, checkout_at, created_at, updated_at)
        VALUES
            (@correlationId, @merchantTransactionId, @flowId, @xCorrelationId, @srcDpaId, @spiToken,
             @amount, @currency, 'Pending', @now, @now, @now) AS new
        ON DUPLICATE KEY UPDATE
            merchant_transaction_id = new.merchant_transaction_id,
            flow_id = new.flow_id,
            x_correlation_id = new.x_correlation_id,
            src_dpa_id = new.src_dpa_id,
            spi_token = new.spi_token,
            transaction_amount = new.transaction_amount,
            transaction_currency = new.transaction_currency,
            checkout_at = new.checkout_at,
            updated_at = new.updated_at
        """;

    // A confirmation for a checkout recorded before the database existed inserts its row. A Confirmed row is never
    // downgraded by a later failed attempt; checkout data already recorded wins over the confirmation's.
    private const string RecordConfirmationSql = """
        INSERT INTO checkout_transactions
            (src_correlation_id, merchant_transaction_id, flow_id, x_correlation_id, transaction_amount, transaction_currency,
             status, confirmation_attempts, last_confirmation_at, confirmed_at,
             checkout_event_type, checkout_event_status, confirmation_status, confirmation_reason,
             network_authorization_code, network_transaction_identifier,
             mastercard_http_status, mastercard_detail, mastercard_flow_id, created_at, updated_at)
        VALUES
            (@correlationId, @merchantTransactionId, @flowId, @xCorrelationId, @amount, @currency,
             @status, 1, @now, @confirmedAt,
             @eventType, @eventStatus, @confirmationStatus, @reason,
             @authorizationCode, @networkTransactionId,
             @httpStatus, @detail, @mastercardFlowId, @now, @now) AS new
        ON DUPLICATE KEY UPDATE
            status = IF(checkout_transactions.status = 'Confirmed', 'Confirmed', new.status),
            confirmation_attempts = checkout_transactions.confirmation_attempts + 1,
            last_confirmation_at = new.last_confirmation_at,
            confirmed_at = COALESCE(checkout_transactions.confirmed_at, new.confirmed_at),
            checkout_event_type = new.checkout_event_type,
            checkout_event_status = new.checkout_event_status,
            confirmation_status = new.confirmation_status,
            confirmation_reason = new.confirmation_reason,
            network_authorization_code = new.network_authorization_code,
            network_transaction_identifier = new.network_transaction_identifier,
            mastercard_http_status = new.mastercard_http_status,
            mastercard_detail = new.mastercard_detail,
            mastercard_flow_id = COALESCE(new.mastercard_flow_id, checkout_transactions.mastercard_flow_id),
            merchant_transaction_id = COALESCE(checkout_transactions.merchant_transaction_id, new.merchant_transaction_id),
            flow_id = COALESCE(checkout_transactions.flow_id, new.flow_id),
            x_correlation_id = COALESCE(checkout_transactions.x_correlation_id, new.x_correlation_id),
            transaction_amount = COALESCE(checkout_transactions.transaction_amount, new.transaction_amount),
            transaction_currency = COALESCE(checkout_transactions.transaction_currency, new.transaction_currency),
            updated_at = new.updated_at
        """;

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(CreateTableSql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecordCheckoutAsync(CompleteCheckoutRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var command = dataSource.CreateCommand(RecordCheckoutSql);
        Add(command, "@correlationId", request.SrcCorrelationId);
        Add(command, "@merchantTransactionId", request.MerchantTransactionId);
        Add(command, "@flowId", request.FlowId);
        Add(command, "@xCorrelationId", request.XCorrelationId);
        Add(command, "@srcDpaId", request.SrcDpaId);
        Add(command, "@spiToken", request.SpiToken);
        Add(command, "@amount", decimal.Round(request.TransactionAmount, 2));
        Add(command, "@currency", request.TransactionCurrencyCode);
        Add(command, "@now", Now());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecordConfirmationAsync(CheckoutConfirmationRequest request, ConfirmationOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(outcome);
        var data = request.ConfirmationData;
        var now = Now();
        await using var command = dataSource.CreateCommand(RecordConfirmationSql);
        Add(command, "@correlationId", request.CorrelationId);
        Add(command, "@merchantTransactionId", request.MerchantTransactionId);
        Add(command, "@flowId", request.FlowId);
        Add(command, "@xCorrelationId", request.XCorrelationId);
        Add(command, "@amount", data?.TransactionAmount is { } amount ? decimal.Round(amount.Amount, 2) : null);
        Add(command, "@currency", data?.TransactionAmount?.CurrencyCode);
        Add(command, "@status", outcome.Confirmed ? CheckoutTransactionStatus.Confirmed : CheckoutTransactionStatus.ConfirmationFailed);
        Add(command, "@now", now);
        Add(command, "@confirmedAt", outcome.Confirmed ? now : null);
        Add(command, "@eventType", data?.CheckoutEventType);
        Add(command, "@eventStatus", data?.CheckoutEventStatus);
        Add(command, "@confirmationStatus", data?.ConfirmationStatus);
        Add(command, "@reason", data?.ConfirmationReason);
        Add(command, "@authorizationCode", data?.NetworkAuthorizationCode);
        Add(command, "@networkTransactionId", data?.NetworkTransactionIdentifier);
        Add(command, "@httpStatus", outcome.Confirmed ? null : outcome.HttpStatus);
        Add(command, "@detail", outcome.Confirmed ? null : Truncate(outcome.Detail, 1000));
        Add(command, "@mastercardFlowId", outcome.FlowId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DateTime Now() => time.GetUtcNow().UtcDateTime;

    // Blank strings are stored as NULL.
    private static void Add(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value is string s && string.IsNullOrWhiteSpace(s) ? DBNull.Value : value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string? Truncate(string? value, int length) => value is { Length: > 0 } && value.Length > length ? value[..length] : value;
}

/// <summary>
/// Records checkout state without ever failing the request: Mastercard has already been called when this runs, so a
/// database error is logged (with the ids needed to fix the row by hand) and swallowed.
/// </summary>
public sealed class CheckoutStateRecorder(ICheckoutTransactionStore store, ILogger<CheckoutStateRecorder> logger)
{
    public async Task CheckoutAsync(CompleteCheckoutRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            await store.RecordCheckoutAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is MySqlException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException)
        {
            logger.LogError(ex, "Checkout {CorrelationId} (x-correlation-id {XCorrelationId}) was not recorded in the database as Pending.",
                request.SrcCorrelationId, request.XCorrelationId);
        }
    }

    public async Task ConfirmationAsync(CheckoutConfirmationRequest request, ConfirmationOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await store.RecordConfirmationAsync(request, outcome, cancellationToken);
        }
        catch (Exception ex) when (ex is MySqlException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException)
        {
            logger.LogError(ex,
                "Confirmation of {CorrelationId} (x-correlation-id {XCorrelationId}, {Status}) was not recorded in the database.",
                request.CorrelationId, request.XCorrelationId, outcome.Confirmed ? "Confirmed" : "ConfirmationFailed");
        }
    }
}
