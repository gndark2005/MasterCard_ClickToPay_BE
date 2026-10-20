using System.Text.Json.Serialization;
using FastEndpoints;
using FluentValidation;
using MC_ClickToPay.Api.Endpoints.Checkout;
using MC_ClickToPay.Services.Confirmations;

namespace MC_ClickToPay.Api.Endpoints.Confirmations;

/// <summary>The SPI token (route) and the PowerTranz payment result (body) of the transaction to confirm.</summary>
public sealed class ConfirmTransactionRequest
{
    /// <summary>PowerTranz SPI token of the transaction, from the route. Never logged or returned.</summary>
    public string SpiToken { get; init; } = string.Empty;

    /// <summary>PowerTranz Approved of the payment (/spi/Payment response). Required.</summary>
    [JsonPropertyName("approved")]
    public bool? Approved { get; init; }

    /// <summary>PowerTranz AuthorizationCode.</summary>
    [JsonPropertyName("authorizationCode")]
    public string? AuthorizationCode { get; init; }

    /// <summary>PowerTranz ResponseMessage.</summary>
    [JsonPropertyName("responseMessage")]
    public string? ResponseMessage { get; init; }

    [JsonPropertyName("networkTransactionIdentifier")]
    public string? NetworkTransactionIdentifier { get; init; }

    public PaymentOutcome ToPaymentOutcome() => new()
    {
        Approved = Approved!.Value,
        AuthorizationCode = AuthorizationCode,
        ResponseMessage = ResponseMessage,
        NetworkTransactionIdentifier = NetworkTransactionIdentifier
    };
}

/// <summary>
/// Confirms one transaction of the daily transaction file to Mastercard POST /srci/api/checkout/confirmations
/// (OAuth 1.0a). Receives the SPI token and the PowerTranz payment result; every other value comes from the file row
/// that holds the SPI token.
/// </summary>
public sealed class ConfirmTransactionEndpoint(TransactionConfirmationService confirmations, ILogger<ConfirmTransactionEndpoint> logger)
    : Endpoint<ConfirmTransactionRequest, TransactionConfirmationResult>
{
    public const string Route = "/api/confirmations/{spiToken}";

    public override void Configure()
    {
        Post(Route);
        Summary(s =>
        {
            s.Summary = "Confirm a transaction to Mastercard by its SPI token";
            s.Description = "Call it after the PowerTranz payment (/spi/Payment), with the SpiToken of its response " +
                "in the route and its result in the body. Finds the row of the daily transaction files " +
                "(ctp_txn_YYYYMMDD.csv, newest first) whose spitoken is the one in the route and sends to Mastercard " +
                "POST /srci/api/checkout/confirmations its SrcCorrelationId, MerchantTransactionId, FlowId and amount " +
                "with the payment outcome: checkoutEventType 01; checkoutEventStatus and confirmationStatus 01 when " +
                "approved, 02 when not; responseMessage as confirmationReason; authorizationCode as " +
                "networkAuthorizationCode. The outcome and the result are written back to the row (outcome columns, " +
                "confirmation_result, confirmation_date, confirmation_time, confirmation_detail) and to the database. " +
                "A row already Confirmed is not sent again; a ConfirmationFailed row can be confirmed again.";
            s.Params["spiToken"] = "PowerTranz SpiToken of the transaction (the one sent to POST /api/checkout).";
            s.ExampleRequest = new ConfirmTransactionRequest
            {
                Approved = true,
                AuthorizationCode = "123456",
                ResponseMessage = "Transaction is approved",
                NetworkTransactionIdentifier = "mastercard"
            };
            s.Response<TransactionConfirmationResult>(200, "Mastercard accepted the confirmation. Headers: X-Src-Cx-Flow-Id, X-Correlation-Id.");
            s.Response(400, "Invalid SPI token or body, or Mastercard rejected the confirmation data (its reason code is included).");
            s.Response(401, "API key is missing, incorrect or not configured.");
            s.Response(404, "No transaction file row has this SPI token, or Mastercard did not find the checkout transaction.");
            s.Response(409, "The transaction was already confirmed, or another request is confirming it.");
            s.Response(422, "The row's data is invalid (amount, currency, ids).");
            s.Response(502, "Mastercard could not be reached, failed, or refused the credentials or DPA.");
            s.Response(503, "Signing key or transaction files are not available.");
        });
    }

    public override async Task HandleAsync(ConfirmTransactionRequest request, CancellationToken ct)
    {
        TransactionConfirmationResult result;
        try
        {
            result = await confirmations.ConfirmAsync(request.SpiToken, request.ToPaymentOutcome(), ct);
        }
        catch (IOException ex)
        {
            logger.LogError(ex, "The transaction files could not be read.");
            await Send.ResultAsync(Results.Problem(statusCode: 503, title: "The transaction files are not available."));
            return;
        }
        catch (Exception ex) when (CheckoutErrors.ToProblem(ex, logger) is { } problem)
        {
            await Send.ResultAsync(problem);
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.XCorrelationId))
        {
            HttpContext.Response.Headers["X-Correlation-Id"] = result.XCorrelationId;
        }

        switch (result.Status)
        {
            case TransactionConfirmationStatus.Confirmed:
                if (!string.IsNullOrWhiteSpace(result.FlowId))
                {
                    HttpContext.Response.Headers["X-Src-Cx-Flow-Id"] = result.FlowId;
                }

                await Send.OkAsync(result, ct);
                break;
            case TransactionConfirmationStatus.Failed:
                await Send.ResultAsync(result.HttpStatus switch
                {
                    400 => Results.Problem(statusCode: 400, title: "Mastercard rejected the confirmation.", detail: result.Detail),
                    404 => Results.Problem(statusCode: 404, title: "Checkout transaction not found by Mastercard.", detail: result.Detail),
                    _ => Results.Problem(statusCode: 502, title: "Mastercard confirmation failed.", detail: result.Detail)
                });
                break;
            case TransactionConfirmationStatus.NotFound:
                await Send.ResultAsync(Results.Problem(statusCode: 404, title: "Transaction not found.", detail: result.Detail));
                break;
            case TransactionConfirmationStatus.AlreadyConfirmed:
            case TransactionConfirmationStatus.InProgress:
                await Send.ResultAsync(Results.Problem(statusCode: 409, title: "Transaction not confirmed again.", detail: result.Detail));
                break;
            default:
                await Send.ResultAsync(Results.Problem(statusCode: 422, title: "Transaction cannot be confirmed.", detail: result.Detail));
                break;
        }
    }
}

public sealed class ConfirmTransactionValidator : Validator<ConfirmTransactionRequest>
{
    public ConfirmTransactionValidator()
    {
        RuleFor(x => x.SpiToken).NotEmpty().MaximumLength(2048);
        RuleFor(x => x.Approved).NotNull().WithMessage("approved is required: the PowerTranz Approved of the payment.");
        RuleFor(x => x.AuthorizationCode).MaximumLength(64);
        RuleFor(x => x.ResponseMessage).MaximumLength(255);
        RuleFor(x => x.NetworkTransactionIdentifier).MaximumLength(255);
    }
}
