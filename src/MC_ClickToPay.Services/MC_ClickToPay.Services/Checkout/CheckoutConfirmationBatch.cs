using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MC_ClickToPay.Services.Abstractions;
using MC_ClickToPay.Services.Exceptions;
using Microsoft.Extensions.Logging;

namespace MC_ClickToPay.Services.Checkout;

/// <summary>What happened to one row of the transaction file.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConfirmationRowStatus>))]
public enum ConfirmationRowStatus
{
    /// <summary>Mastercard accepted the confirmation (204).</summary>
    Confirmed,

    /// <summary>Mastercard rejected it or could not be reached.</summary>
    Failed,

    /// <summary>The row's data is invalid; nothing was sent.</summary>
    Invalid,

    /// <summary>The row has no payment outcome yet (outcome columns empty); nothing was sent.</summary>
    Skipped
}

public sealed class ConfirmationRowResult
{
    [JsonPropertyName("line")]
    public required int Line { get; init; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("xCorrelationId")]
    public string? XCorrelationId { get; init; }

    [JsonPropertyName("status")]
    public required ConfirmationRowStatus Status { get; init; }

    /// <summary>For Failed rows: the status the single confirmation would return (400, 404 or 502).</summary>
    [JsonPropertyName("httpStatus")]
    public int? HttpStatus { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    /// <summary>For Confirmed rows: the X-Src-Cx-Flow-Id Mastercard returned.</summary>
    [JsonPropertyName("flowId")]
    public string? FlowId { get; init; }
}

public sealed class ConfirmationBatchResult
{
    [JsonPropertyName("fileName")]
    public string? FileName { get; init; }

    [JsonPropertyName("totalRows")]
    public required int TotalRows { get; init; }

    [JsonPropertyName("confirmed")]
    public required int Confirmed { get; init; }

    [JsonPropertyName("failed")]
    public required int Failed { get; init; }

    [JsonPropertyName("invalid")]
    public required int Invalid { get; init; }

    [JsonPropertyName("skipped")]
    public required int Skipped { get; init; }

    [JsonPropertyName("results")]
    public required IReadOnlyList<ConfirmationRowResult> Results { get; init; }
}

/// <summary>
/// Confirms every row of an uploaded transaction file to Mastercard /checkout/confirmations, with the same mapping as
/// the single confirmation. A row's failure never stops the others; only a missing signing key (or any other non
/// Mastercard error) aborts the batch. Results keep the file order.
/// </summary>
public sealed partial class CheckoutConfirmationBatch(
    IMastercardCheckoutService checkout, CheckoutStateRecorder state, ILogger<CheckoutConfirmationBatch> logger)
{
    /// <summary>Concurrent Mastercard calls: enough for a daily file, gentle on the sandbox.</summary>
    public const int MaxConcurrency = 4;

    // "31.25" as the file writes it: a dot, no thousands separators (so "12,50" is invalid, not 1250).
    private const NumberStyles AmountStyle = NumberStyles.AllowDecimalPoint;

    public async Task<ConfirmationBatchResult> ProcessAsync(
        IReadOnlyList<CheckoutTransactionRow> rows, string? fileName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var results = new ConfirmationRowResult[rows.Count];
        using var gate = new SemaphoreSlim(MaxConcurrency);

        var tasks = rows.Select(async (row, index) =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                results[index] = await ProcessRowAsync(row, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);

        var result = new ConfirmationBatchResult
        {
            FileName = fileName,
            TotalRows = rows.Count,
            Confirmed = results.Count(r => r.Status == ConfirmationRowStatus.Confirmed),
            Failed = results.Count(r => r.Status == ConfirmationRowStatus.Failed),
            Invalid = results.Count(r => r.Status == ConfirmationRowStatus.Invalid),
            Skipped = results.Count(r => r.Status == ConfirmationRowStatus.Skipped),
            Results = results
        };
        logger.LogInformation(
            "Confirmation batch {FileName}: {Total} rows, {Confirmed} confirmed, {Failed} failed, {Invalid} invalid, {Skipped} skipped.",
            fileName ?? "-", result.TotalRows, result.Confirmed, result.Failed, result.Invalid, result.Skipped);
        return result;
    }

    private async Task<ConfirmationRowResult> ProcessRowAsync(CheckoutTransactionRow row, CancellationToken cancellationToken)
    {
        ConfirmationRowResult Result(ConfirmationRowStatus status, string? detail = null, int? httpStatus = null, string? flowId = null) => new()
        {
            Line = row.Line,
            CorrelationId = row.SrcCorrelationId,
            XCorrelationId = row.XCorrelationId,
            Status = status,
            Detail = detail,
            HttpStatus = httpStatus,
            FlowId = flowId
        };

        if (row.CheckoutEventType is null && row.CheckoutEventStatus is null && row.ConfirmationStatus is null)
        {
            return Result(ConfirmationRowStatus.Skipped,
                "No payment outcome: checkout_event_type, checkout_event_status and confirmation_status are empty.");
        }

        var errors = Validate(row);
        if (errors.Count > 0)
        {
            return Result(ConfirmationRowStatus.Invalid, string.Join(" ", errors));
        }

        var request = new CheckoutConfirmationRequest
        {
            CorrelationId = row.SrcCorrelationId!,
            MerchantTransactionId = row.MerchantTransactionId,
            FlowId = row.FlowId,
            XCorrelationId = row.XCorrelationId ?? $"batch-line-{row.Line}",
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

        try
        {
            var confirmation = await checkout.ConfirmAsync(request, cancellationToken);
            await state.ConfirmationAsync(request, new ConfirmationOutcome(true, confirmation.FlowId, null, null), cancellationToken);
            return Result(ConfirmationRowStatus.Confirmed, flowId: confirmation.FlowId);
        }
        catch (MastercardCheckoutException ex)
        {
            logger.LogWarning("Confirmation batch line {Line} ({CorrelationId}): {Message}", row.Line, row.SrcCorrelationId, ex.Message);
            var httpStatus = HttpStatusFor(ex.StatusCode);
            await state.ConfirmationAsync(request, new ConfirmationOutcome(false, null, httpStatus, ex.Message), cancellationToken);
            return Result(ConfirmationRowStatus.Failed, ex.Message, httpStatus);
        }
    }

    /// <summary>Same mapping as the single confirmation endpoint.</summary>
    public static int HttpStatusFor(int? mastercardStatus) => mastercardStatus switch
    {
        400 => 400,
        404 => 404,
        _ => 502
    };

    // Same rules as the single confirmation (ConfirmCheckoutValidator), so a row behaves like a JSON request.
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
