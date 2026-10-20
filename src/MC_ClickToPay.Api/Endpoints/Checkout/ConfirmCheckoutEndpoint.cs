using System.Text.Json.Serialization;
using FastEndpoints;
using FluentValidation;
using MC_ClickToPay.Services.Abstractions;
using MC_ClickToPay.Services.Checkout;
using MC_ClickToPay.Services.Exceptions;

namespace MC_ClickToPay.Api.Endpoints.Checkout;

/// <summary>
/// Body of POST /api/checkout/confirmations, in one of two forms:
/// application/json with one confirmation (the Mastercard body plus flowId and xCorrelationId), or
/// multipart/form-data with the daily transaction file in <see cref="File"/> to confirm all its rows (batch).
/// </summary>
public sealed class ConfirmCheckoutRequest
{
    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("merchantTransactionId")]
    public string? MerchantTransactionId { get; init; }

    [JsonPropertyName("flowId")]
    public string? FlowId { get; init; }

    [JsonPropertyName("xCorrelationId")]
    public string? XCorrelationId { get; init; }

    [JsonPropertyName("confirmationData")]
    public ConfirmationData? ConfirmationData { get; init; }

    /// <summary>Optional: the daily transaction file (ctp_txn_YYYYMMDD.csv), multipart field "file". When present, every row is confirmed.</summary>
    [BindFrom("file")]
    public IFormFile? File { get; init; }

    public CheckoutConfirmationRequest ToConfirmation() => new()
    {
        CorrelationId = CorrelationId!,
        MerchantTransactionId = MerchantTransactionId,
        FlowId = FlowId,
        XCorrelationId = XCorrelationId!,
        ConfirmationData = ConfirmationData
    };
}

/// <summary>
/// Reports the outcome of a checkout or payment to Mastercard POST /srci/api/checkout/confirmations (OAuth 1.0a):
/// one confirmation from a JSON body, or every row of an uploaded daily transaction file (batch).
/// </summary>
public sealed class ConfirmCheckoutEndpoint(
    IMastercardCheckoutService checkout, CheckoutConfirmationBatch batch, CheckoutStateRecorder state,
    ILogger<ConfirmCheckoutEndpoint> logger)
    : Endpoint<ConfirmCheckoutRequest>
{
    public const string Route = "/api/checkout/confirmations";

    /// <summary>Body limit for file uploads on this route (the API default is 256 KiB).</summary>
    public const long MaximumFileUploadBytes = 10 * 1024 * 1024;

    public override void Configure()
    {
        Post(Route);
        AllowFileUploads();
        Description(b => b.Accepts<ConfirmCheckoutRequest>("application/json", "multipart/form-data"));
        Summary(s =>
        {
            s.Summary = "Confirm Click to Pay checkouts to Mastercard (one, or a batch file)";
            s.Description = "Sends payment outcomes to Mastercard POST /srci/api/checkout/confirmations.\n\n" +
                "**One confirmation** (application/json): Mastercard's body (correlationId, merchantTransactionId, " +
                "confirmationData) plus flowId, sent as the X-Src-Cx-Flow-Id header, and xCorrelationId, the caller's " +
                "tracing id. merchantTransactionId is also sent as the merchant-transaction-id header. " +
                "confirmationTimestamp defaults to now (UTC). Returns 204 with the X-Src-Cx-Flow-Id Mastercard answered.\n\n" +
                "**Batch** (multipart/form-data): the daily transaction file ctp_txn_YYYYMMDD.csv in the `file` field. " +
                "Each row is confirmed with its SrcCorrelationId, MerchantTransactionId, FlowId, XCorrelationId, amount " +
                "and the outcome columns (checkout_event_type, checkout_event_status, confirmation_status, " +
                "confirmation_reason, network_authorization_code, network_transaction_identifier). Rows without an " +
                "outcome are skipped. Returns 200 with one result per row (Confirmed, Failed, Invalid, Skipped).";
            s.ExampleRequest = new ConfirmCheckoutRequest
            {
                CorrelationId = "43465920-50e4-475f-a8d8-198a842fd3bf",
                MerchantTransactionId = "0a4e0d3.34f4a04b.bddaa2cf22b394705447f2b03ca662eb86d0b253",
                FlowId = "34f4a04b.2ce61515-55b7-4c09-845b-45492c2ef327.1790888627",
                XCorrelationId = "7d1f6c2e-9a43-4b8e-b1a2-3f5c8e0d4a17",
                ConfirmationData = new ConfirmationData
                {
                    CheckoutEventType = "01",
                    CheckoutEventStatus = "01",
                    ConfirmationStatus = "01",
                    ConfirmationReason = "The payment was successful",
                    ConfirmationTimestamp = new DateTimeOffset(2026, 10, 15, 1, 15, 0, TimeSpan.Zero),
                    NetworkAuthorizationCode = "123456",
                    NetworkTransactionIdentifier = "mastercard",
                    TransactionAmount = new ConfirmationAmount { Amount = 31.25m, CurrencyCode = "USD" }
                }
            };
            s.Response<ConfirmationBatchResult>(200, "Batch processed: one result per row.");
            s.Response(204, "One confirmation accepted by Mastercard. Headers: X-Src-Cx-Flow-Id, X-Correlation-Id.");
            s.Response(400, "Invalid request or file, or Mastercard rejected the confirmation data (its reason code is included).");
            s.Response(401, "API key is missing, incorrect or not configured.");
            s.Response(404, "One confirmation: Mastercard did not find the checkout transaction.");
            s.Response(413, "The uploaded file exceeds 10 MiB.");
            s.Response(502, "One confirmation: Mastercard could not be reached, failed, or refused the credentials or DPA.");
            s.Response(503, "Signing key is not configured or unavailable.");
        });
    }

    public override Task HandleAsync(ConfirmCheckoutRequest request, CancellationToken ct) =>
        request.File is null ? ConfirmOneAsync(request, ct) : ConfirmFileAsync(request.File, ct);

    private async Task ConfirmOneAsync(ConfirmCheckoutRequest request, CancellationToken ct)
    {
        HttpContext.Response.Headers["X-Correlation-Id"] = request.XCorrelationId;
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["XCorrelationId"] = request.XCorrelationId! });
        var data = request.ConfirmationData!;
        var confirmation = request.ToConfirmation();
        try
        {
            var result = await checkout.ConfirmAsync(confirmation, ct);
            await state.ConfirmationAsync(confirmation, new ConfirmationOutcome(true, result.FlowId, null, null), ct);
            logger.LogInformation(
                "Checkout {CorrelationId} (x-correlation-id {XCorrelationId}) confirmed to Mastercard: eventType {EventType}, eventStatus {EventStatus}, confirmationStatus {ConfirmationStatus}.",
                request.CorrelationId, request.XCorrelationId, data.CheckoutEventType, data.CheckoutEventStatus,
                data.ConfirmationStatus);

            if (!string.IsNullOrWhiteSpace(result.FlowId))
            {
                HttpContext.Response.Headers["X-Src-Cx-Flow-Id"] = result.FlowId;
            }

            await Send.NoContentAsync(ct);
        }
        catch (MastercardCheckoutException ex)
        {
            logger.LogWarning("Checkout {CorrelationId} (x-correlation-id {XCorrelationId}): Mastercard confirmation failed: {Message}",
                request.CorrelationId, request.XCorrelationId, ex.Message);
            var status = CheckoutConfirmationBatch.HttpStatusFor(ex.StatusCode);
            await state.ConfirmationAsync(confirmation, new ConfirmationOutcome(false, null, status, ex.Message), ct);
            await Send.ResultAsync(status switch
            {
                400 => Results.Problem(statusCode: 400, title: "Mastercard rejected the confirmation.", detail: ex.Message),
                404 => Results.Problem(statusCode: 404, title: "Checkout transaction not found by Mastercard.", detail: ex.Message),
                _ => Results.Problem(statusCode: 502, title: "Mastercard confirmation failed.", detail: ex.Message)
            });
        }
        catch (Exception ex) when (CheckoutErrors.ToProblem(ex, logger) is { } problem)
        {
            await Send.ResultAsync(problem);
        }
    }

    private async Task ConfirmFileAsync(IFormFile file, CancellationToken ct)
    {
        IReadOnlyList<CheckoutTransactionRow> rows;
        try
        {
            await using var stream = file.OpenReadStream();
            rows = await CheckoutTransactionFileReader.ReadAsync(stream, ct);
        }
        catch (InvalidTransactionFileException ex)
        {
            await Send.ResultAsync(Results.Problem(statusCode: 400, title: "Invalid transaction file.", detail: ex.Message));
            return;
        }

        // Only the name's last segment: never echo client paths.
        var fileName = Path.GetFileName(file.FileName);
        logger.LogInformation("Confirmation batch {FileName}: {Rows} rows received.", fileName, rows.Count);
        try
        {
            await Send.OkAsync(await batch.ProcessAsync(rows, fileName, ct), ct);
        }
        catch (Exception ex) when (CheckoutErrors.ToProblem(ex, logger) is { } problem)
        {
            await Send.ResultAsync(problem);
        }
    }
}

public sealed class ConfirmCheckoutValidator : Validator<ConfirmCheckoutRequest>
{
    private const string TwoDigits = "^[0-9]{2}$";

    public ConfirmCheckoutValidator()
    {
        // Batch: only the file matters.
        When(x => x.File is not null, () =>
        {
            RuleFor(x => x.File!.Length).GreaterThan(0).WithName("file").WithMessage("file is empty.");
            RuleFor(x => x.File!.FileName).Must(name => Path.GetExtension(name).Equals(".csv", StringComparison.OrdinalIgnoreCase))
                .WithName("file").WithMessage("file must be a .csv transaction file (ctp_txn_YYYYMMDD.csv).");
        });

        // One confirmation.
        When(x => x.File is null, () =>
        {
            RuleFor(x => x.CorrelationId).NotEmpty().MaximumLength(256);
            RuleFor(x => x.MerchantTransactionId).MaximumLength(255);
            RuleFor(x => x.FlowId).MaximumLength(256);
            // Echoed in a response header: visible ASCII only.
            RuleFor(x => x.XCorrelationId).NotEmpty().MaximumLength(256).Matches("^[\\x21-\\x7E]+$")
                .WithMessage("xCorrelationId must be visible ASCII characters without spaces.");
            RuleFor(x => x.ConfirmationData).NotNull();
            RuleFor(x => x.ConfirmationData!).ChildRules(data =>
            {
                data.RuleFor(d => d.CheckoutEventType).NotEmpty().Matches(TwoDigits)
                    .WithMessage("checkoutEventType must be a two-digit code, e.g. 01.");
                data.RuleFor(d => d.CheckoutEventStatus).NotEmpty().Matches(TwoDigits)
                    .WithMessage("checkoutEventStatus must be a two-digit code, e.g. 01.");
                data.RuleFor(d => d.ConfirmationStatus).NotEmpty().Matches(TwoDigits)
                    .WithMessage("confirmationStatus must be a two-digit code, e.g. 01.");
                data.RuleFor(d => d.ConfirmationReason).MaximumLength(255);
                data.RuleFor(d => d.NetworkAuthorizationCode).MaximumLength(64);
                data.RuleFor(d => d.NetworkTransactionIdentifier).MaximumLength(255);
                data.RuleFor(d => d.TransactionAmount!).ChildRules(amount =>
                {
                    amount.RuleFor(a => a.Amount).GreaterThan(0).PrecisionScale(12, 2, ignoreTrailingZeros: true);
                    amount.RuleFor(a => a.CurrencyCode).NotEmpty().Matches("^[A-Z]{3}$")
                        .WithMessage("transactionCurrencyCode must be an ISO 4217 alphabetic code, e.g. USD.");
                }).When(d => d.TransactionAmount is not null);
            }).When(x => x.ConfirmationData is not null);
        });
    }
}
