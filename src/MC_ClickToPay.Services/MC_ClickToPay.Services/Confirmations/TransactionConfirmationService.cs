using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MC_ClickToPay.Services.Abstractions;
using MC_ClickToPay.Services.Checkout;
using MC_ClickToPay.Services.Exceptions;
using Microsoft.Extensions.Logging;

namespace MC_ClickToPay.Services.Confirmations;

/// <summary>What happened to the transaction of an SPI token.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TransactionConfirmationStatus>))]
public enum TransactionConfirmationStatus
{
    /// <summary>Mastercard accepted the confirmation (204).</summary>
    Confirmed,

    /// <summary>Mastercard rejected it or could not be reached; it can be confirmed again.</summary>
    Failed,

    /// <summary>No transaction file row has this SPI token.</summary>
    NotFound,

    /// <summary>The row was already confirmed; nothing was sent.</summary>
    AlreadyConfirmed,

    /// <summary>Another request is confirming the same SPI token; nothing was sent.</summary>
    InProgress,

    /// <summary>The row's data is invalid; nothing was sent.</summary>
    Invalid
}

/// <summary>
/// The PowerTranz payment result (/spi/Payment response) the caller sends with the SPI token. Mapped to Mastercard's
/// codes: checkoutEventType 01; approved → checkoutEventStatus and confirmationStatus 01, declined → 02.
/// </summary>
public sealed class PaymentOutcome
{
    /// <summary>PowerTranz Approved.</summary>
    [JsonPropertyName("approved")]
    public bool Approved { get; init; }

    /// <summary>PowerTranz AuthorizationCode; sent as networkAuthorizationCode.</summary>
    [JsonPropertyName("authorizationCode")]
    public string? AuthorizationCode { get; init; }

    /// <summary>PowerTranz ResponseMessage; sent as confirmationReason.</summary>
    [JsonPropertyName("responseMessage")]
    public string? ResponseMessage { get; init; }

    /// <summary>Optional networkTransactionIdentifier, e.g. "mastercard".</summary>
    [JsonPropertyName("networkTransactionIdentifier")]
    public string? NetworkTransactionIdentifier { get; init; }
}

/// <summary>Result of confirming one transaction. Never holds the SPI token.</summary>
public sealed class TransactionConfirmationResult
{
    [JsonPropertyName("status")]
    public required TransactionConfirmationStatus Status { get; init; }

    /// <summary>The transaction file holding the row (ctp_txn_YYYYMMDD.csv).</summary>
    [JsonPropertyName("fileName")]
    public string? FileName { get; init; }

    [JsonPropertyName("line")]
    public int? Line { get; init; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("xCorrelationId")]
    public string? XCorrelationId { get; init; }

    /// <summary>For Confirmed: the X-Src-Cx-Flow-Id Mastercard returned.</summary>
    [JsonPropertyName("flowId")]
    public string? FlowId { get; init; }

    /// <summary>For Failed: the status the API answers (400, 404 or 502).</summary>
    [JsonPropertyName("httpStatus")]
    public int? HttpStatus { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }
}

/// <summary>
/// Confirms the transaction of one SPI token to Mastercard /checkout/confirmations. The checkout data comes from the
/// daily transaction file row with that SPI token (newest file first); the payment outcome is the PowerTranz result the
/// caller sends. The outcome and the result are recorded in the database and written back to the row (outcome columns,
/// confirmation_result, date, time, detail). A Confirmed row is not sent again.
/// </summary>
public sealed partial class TransactionConfirmationService(
    IMastercardCheckoutService checkout, ICheckoutTransactionFile transactions, CheckoutStateRecorder state,
    ILogger<TransactionConfirmationService> logger)
{
    // SPI tokens being confirmed in this process: a second request for the same token is refused, not sent twice.
    private static readonly ConcurrentDictionary<string, byte> InProgress = new(StringComparer.Ordinal);

    // "31.25" as the file writes it: a dot, no thousands separators (so "12,50" is invalid, not 1250).
    private const NumberStyles AmountStyle = NumberStyles.AllowDecimalPoint;

    public async Task<TransactionConfirmationResult> ConfirmAsync(string spiToken, PaymentOutcome payment,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(spiToken);
        ArgumentNullException.ThrowIfNull(payment);
        if (!InProgress.TryAdd(spiToken, 0))
        {
            return new TransactionConfirmationResult
            {
                Status = TransactionConfirmationStatus.InProgress,
                Detail = "This transaction is being confirmed by another request."
            };
        }

        try
        {
            var row = await transactions.FindAsync(spiToken, cancellationToken);
            return row is null
                ? new TransactionConfirmationResult
                {
                    Status = TransactionConfirmationStatus.NotFound,
                    Detail = "No transaction file row has this SPI token."
                }
                : await ConfirmRowAsync(row, payment, cancellationToken);
        }
        finally
        {
            InProgress.TryRemove(spiToken, out _);
        }
    }

    private async Task<TransactionConfirmationResult> ConfirmRowAsync(CheckoutTransactionRow fileRow, PaymentOutcome payment,
        CancellationToken cancellationToken)
    {
        var row = WithOutcome(fileRow, payment);

        TransactionConfirmationResult Result(TransactionConfirmationStatus status, string? detail = null, int? httpStatus = null,
            string? flowId = null)
        {
            return new()
            {
                Status = status,
                FileName = row.FileName,
                Line = row.Line,
                CorrelationId = row.SrcCorrelationId,
                XCorrelationId = row.XCorrelationId,
                Detail = detail,
                HttpStatus = httpStatus,
                FlowId = flowId
            };
        }

        if (string.Equals(row.ConfirmationResult, CheckoutTransactionStatus.Confirmed, StringComparison.OrdinalIgnoreCase))
        {
            return Result(TransactionConfirmationStatus.AlreadyConfirmed, "The transaction was already confirmed to Mastercard.");
        }

        var errors = Validate(row);
        if (errors.Count > 0)
        {
            return Result(TransactionConfirmationStatus.Invalid, string.Join(" ", errors));
        }

        var request = ToConfirmation(row);
        TransactionConfirmationResult result;
        ConfirmationOutcome outcome;
        try
        {
            var confirmation = await checkout.ConfirmAsync(request, cancellationToken);
            outcome = new ConfirmationOutcome(true, confirmation.FlowId, null, null);
            result = Result(TransactionConfirmationStatus.Confirmed, flowId: confirmation.FlowId);
            logger.LogInformation(
                "Transaction {FileName} line {Line} ({CorrelationId}, x-correlation-id {XCorrelationId}) confirmed to Mastercard: eventType {EventType}, eventStatus {EventStatus}, confirmationStatus {ConfirmationStatus}.",
                row.FileName, row.Line, row.SrcCorrelationId, request.XCorrelationId, row.CheckoutEventType,
                row.CheckoutEventStatus, row.ConfirmationStatus);
        }
        catch (MastercardCheckoutException ex)
        {
            logger.LogWarning("Transaction {FileName} line {Line} ({CorrelationId}): Mastercard confirmation failed: {Message}",
                row.FileName, row.Line, row.SrcCorrelationId, ex.Message);
            var httpStatus = HttpStatusFor(ex.StatusCode);
            outcome = new ConfirmationOutcome(false, null, httpStatus, ex.Message);
            result = Result(TransactionConfirmationStatus.Failed, ex.Message, httpStatus);
        }

        // Mastercard has been called: the records are written even if the caller has gone, and never fail the result.
        await state.ConfirmationAsync(request, outcome, CancellationToken.None);
        await RecordInFileAsync(row, outcome);
        return result;
    }

    private async Task RecordInFileAsync(CheckoutTransactionRow row, ConfirmationOutcome outcome)
    {
        var confirmation = new TransactionFileConfirmation(
            outcome.Confirmed ? CheckoutTransactionStatus.Confirmed : CheckoutTransactionStatus.ConfirmationFailed,
            outcome.Confirmed ? null : outcome.Detail);
        try
        {
            await transactions.RecordConfirmationAsync(row, confirmation, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or InvalidTransactionFileException)
        {
            logger.LogError(ex,
                "Confirmation of {CorrelationId} ({Result}) was not written to {FileName} line {Line}; update the row by hand.",
                row.SrcCorrelationId, confirmation.Result, row.FileName, row.Line);
        }
    }

    // The file row with the outcome columns taken from the PowerTranz result (blank values become null).
    private static CheckoutTransactionRow WithOutcome(CheckoutTransactionRow row, PaymentOutcome payment)
    {
        static string? Value(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var status = payment.Approved ? "01" : "02";
        return new CheckoutTransactionRow
        {
            FileName = row.FileName,
            Line = row.Line,
            SpiToken = row.SpiToken,
            TransactionAmount = row.TransactionAmount,
            TransactionCurrency = row.TransactionCurrency,
            SrcCorrelationId = row.SrcCorrelationId,
            MerchantTransactionId = row.MerchantTransactionId,
            FlowId = row.FlowId,
            XCorrelationId = row.XCorrelationId,
            CheckoutEventType = "01",
            CheckoutEventStatus = status,
            ConfirmationStatus = status,
            ConfirmationReason = Value(payment.ResponseMessage),
            NetworkAuthorizationCode = Value(payment.AuthorizationCode),
            NetworkTransactionIdentifier = Value(payment.NetworkTransactionIdentifier),
            ConfirmationResult = row.ConfirmationResult
        };
    }

    private static CheckoutConfirmationRequest ToConfirmation(CheckoutTransactionRow row) => new()
    {
        CorrelationId = row.SrcCorrelationId!,
        MerchantTransactionId = row.MerchantTransactionId,
        FlowId = row.FlowId,
        XCorrelationId = row.XCorrelationId ?? $"txn-line-{row.Line}",
        ConfirmationData = new ConfirmationData
        {
            CheckoutEventType = row.CheckoutEventType!,
            CheckoutEventStatus = row.CheckoutEventStatus!,
            ConfirmationStatus = row.ConfirmationStatus!,
            ConfirmationReason = row.ConfirmationReason,
            NetworkAuthorizationCode = row.NetworkAuthorizationCode,
            NetworkTransactionIdentifier = row.NetworkTransactionIdentifier,
            TransactionAmount = row.TransactionAmount is null
                ? null
                : new ConfirmationAmount
                {
                    Amount = decimal.Parse(row.TransactionAmount, AmountStyle, CultureInfo.InvariantCulture),
                    CurrencyCode = row.TransactionCurrency!
                }
        }
    };

    /// <summary>Mastercard status → API status: its 400 and 404 pass through, anything else is 502.</summary>
    public static int HttpStatusFor(int? mastercardStatus) => mastercardStatus switch
    {
        400 => 400,
        404 => 404,
        _ => 502
    };

    private static List<string> Validate(CheckoutTransactionRow row)
    {
        var errors = new List<string>();
        void Check(bool valid, string message)
        {
            if (!valid)
            {
                errors.Add(message);
            }
        }

        Check(row.SrcCorrelationId is { Length: <= 256 }, "SrcCorrelationId is required (up to 256 characters).");
        Check(row.MerchantTransactionId is null or { Length: <= 255 }, "MerchantTransactionId must be up to 255 characters.");
        Check(row.FlowId is null or { Length: <= 256 }, "FlowId must be up to 256 characters.");
        Check(row.XCorrelationId is null || VisibleAscii().IsMatch(row.XCorrelationId),
            "XCorrelationId must be visible ASCII characters without spaces (up to 256).");
        Check(row.CheckoutEventType is not null && TwoDigits().IsMatch(row.CheckoutEventType), "checkout_event_type must be a two-digit code, e.g. 01.");
        Check(row.CheckoutEventStatus is not null && TwoDigits().IsMatch(row.CheckoutEventStatus), "checkout_event_status must be a two-digit code, e.g. 01.");
        Check(row.ConfirmationStatus is not null && TwoDigits().IsMatch(row.ConfirmationStatus), "confirmation_status must be a two-digit code, e.g. 01.");
        Check(row.ConfirmationReason is null or { Length: <= 255 }, "confirmation_reason must be up to 255 characters.");
        Check(row.NetworkAuthorizationCode is null or { Length: <= 64 }, "network_authorization_code must be up to 64 characters.");
        Check(row.NetworkTransactionIdentifier is null or { Length: <= 255 }, "network_transaction_identifier must be up to 255 characters.");

        if (row.TransactionAmount is not null)
        {
            Check(decimal.TryParse(row.TransactionAmount, AmountStyle, CultureInfo.InvariantCulture, out var amount)
                && amount > 0 && decimal.Round(amount, 2) == amount,
                "transaction_amount must be a positive amount with up to two decimals and a dot, e.g. 31.25.");
            Check(row.TransactionCurrency is not null && Currency().IsMatch(row.TransactionCurrency),
                "transaction_currency must be an ISO 4217 alphabetic code, e.g. USD.");
        }

        return errors;
    }

    [GeneratedRegex("^[0-9]{2}$")]
    private static partial Regex TwoDigits();

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex Currency();

    [GeneratedRegex("^[\\x21-\\x7E]{1,256}$")]
    private static partial Regex VisibleAscii();
}
