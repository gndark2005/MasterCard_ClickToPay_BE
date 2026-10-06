using FastEndpoints;
using MC_ClickToPay.PaymentDemo.Api.Keys;
using MC_ClickToPay.Services.Exceptions;
using MC_ClickToPay.Services.Payments;

namespace MC_ClickToPay.PaymentDemo.Api.Endpoints;

/// <summary>Demo copy of MC_ClickToPay.Api POST /api/payments/confirm: same shared flow, demo key provider.</summary>
public sealed class ConfirmPaymentEndpoint(
    PaymentConfirmationService confirmations, TimeProvider time, ILogger<ConfirmPaymentEndpoint> logger)
    : Endpoint<ConfirmPaymentRequest, ConfirmPaymentResponse>
{
    public const string Route = "/api/demo/payments/confirm";

    public override void Configure()
    {
        Post(Route);
        Summary(s =>
        {
            s.Summary = "Confirm a payment from an encrypted Click to Pay payload (demo)";
            s.Description = "Decrypts encryptedPayload (compact JWE, RSA-OAEP-256 / A128CBC-HS256) with the existing " +
                "MC_ClickToPay.Services decryption, validates the payment data and hands it to the payment processor. " +
                "The processor is SIMULATED in this demo: no PowerTranz call is made and no money moves.";
            s.ExampleRequest = new ConfirmPaymentRequest
            {
                EncryptedPayload = "<header>.<encrypted-key>.<iv>.<ciphertext>.<authentication-tag>",
                TransactionAmount = 31.25m,
                TransactionCurrencyCode = "USD",
                OrderId = "ORDER-1001",
                Eci = "06"
            };
            s.Response<ConfirmPaymentResponse>(200, "Processed: approved, or declined (approved = false).");
            s.Response(400, "missing_payload, invalid_request, invalid_payload or decryption_failed.");
            s.Response(401, "API key is missing, incorrect or not configured.");
            s.Response(413, "Request body exceeds the 256 KiB limit.");
            s.Response(422, "invalid_payment_data: the payload decrypted but its payment data is invalid.");
            s.Response(500, "unexpected_error.");
            s.Response(502, "payment_processing_failed: the processor could not give a result.");
            s.Response(503, "decryption_unavailable: no decryption key/certificate is configured or usable.");
        });
    }

    public override async Task HandleAsync(ConfirmPaymentRequest request, CancellationToken ct)
    {
        if (ConfirmPaymentRequestRules.IsPayloadMissing(request))
        {
            await Send.ResultAsync(DemoProblems.Create(400, DemoProblems.MissingPayload,
                "Missing encrypted payload.", "encryptedPayload is required."));
            return;
        }

        var errors = ConfirmPaymentRequestRules.Validate(request);
        if (errors.Count > 0)
        {
            await Send.ResultAsync(DemoProblems.Create(400, DemoProblems.InvalidRequest,
                "Invalid request.", "One or more request fields are invalid.", errors));
            return;
        }

        var orderId = request.OrderId ?? ConfirmPaymentRequestRules.NewOrderId(time);
        try
        {
            var confirmation = await confirmations.ConfirmAsync(request, orderId, ct);
            await Send.OkAsync(ConfirmPaymentResponse.From(confirmation), ct);
        }
        catch (PayloadDecryptionException ex)
        {
            logger.LogWarning("Order {OrderId}: encrypted payload rejected ({Reason}).", orderId, ex.Error);
            await Send.ResultAsync(ex.Error switch
            {
                PayloadDecryptionError.DecryptionFailed => DemoProblems.Create(400, DemoProblems.DecryptionFailed,
                    "The encrypted payload could not be decrypted.",
                    "It was not encrypted for the configured key, or it was modified."),
                PayloadDecryptionError.InvalidPayload => DemoProblems.Create(422, DemoProblems.InvalidPaymentData,
                    "Invalid decrypted payment data.",
                    "The decrypted payload is not a supported payment: it needs token + cryptogram, or card (dynamicDataType NONE)."),
                _ => DemoProblems.Create(400, DemoProblems.InvalidPayload, "Invalid encrypted payload.",
                    "Provide a five-part compact JWE using RSA-OAEP-256 / A128CBC-HS256.")
            });
        }
        catch (DemoKeyUnavailableException)
        {
            logger.LogWarning("Order {OrderId}: no usable payload decryption key is configured.", orderId);
            await Send.ResultAsync(DemoProblems.Create(503, DemoProblems.DecryptionUnavailable,
                "Payload decryption is temporarily unavailable."));
        }
        catch (InvalidPaymentDataException ex)
        {
            logger.LogWarning("Order {OrderId}: decrypted payment data rejected ({ErrorCount} rule(s)).",
                orderId, ex.Errors.Count);
            await Send.ResultAsync(DemoProblems.Create(422, DemoProblems.InvalidPaymentData,
                "Invalid decrypted payment data.", ex.Message, ex.Errors));
        }
        catch (PaymentProcessingException ex)
        {
            logger.LogWarning("Order {OrderId}: payment processing failed: {Reason}", orderId, ex.Message);
            await Send.ResultAsync(DemoProblems.Create(502, DemoProblems.PaymentProcessingFailed,
                "Payment processing failed.", ex.Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Only the type: messages of unexpected exceptions are not guaranteed to be free of payment data.
            logger.LogError("Order {OrderId}: unexpected {ExceptionType}.", orderId, ex.GetType().FullName);
            await Send.ResultAsync(DemoProblems.Create(500, DemoProblems.UnexpectedError,
                "An unexpected error occurred.", $"Reference the order id {orderId} when reporting it."));
        }
    }
}
