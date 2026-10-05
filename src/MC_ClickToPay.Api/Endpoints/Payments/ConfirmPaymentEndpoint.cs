using FastEndpoints;
using MC_ClickToPay.Api.Keys;
using MC_ClickToPay.Services.Exceptions;
using MC_ClickToPay.Services.Payments;

namespace MC_ClickToPay.Api.Endpoints.Payments;

public sealed class ConfirmPaymentEndpoint(
    PaymentConfirmationService confirmations, TimeProvider time, ILogger<ConfirmPaymentEndpoint> logger)
    : Endpoint<ConfirmPaymentRequest, ConfirmPaymentResponse>
{
    public const string Route = "/api/payments/confirm";

    public override void Configure()
    {
        Post(Route);
        Summary(s =>
        {
            s.Summary = "Confirm a payment from a Mastercard encryptedPayload";
            s.Description = "Takes the encryptedPayload returned by Mastercard /checkout (or by POST /api/checkout/complete " +
                "upstream), decrypts it with the configured Payload Encryption key, validates the payment data, maps it " +
                "to a PowerTranz-style request and hands it to the payment processor. The processor is SIMULATED until " +
                "PowerTranz is plugged in: no money moves and the response says simulated = true.";
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
            s.Response(503, "decryption_unavailable: the decryption certificate cannot be loaded.");
        });
    }

    public override async Task HandleAsync(ConfirmPaymentRequest request, CancellationToken ct)
    {
        if (ConfirmPaymentRequestRules.IsPayloadMissing(request))
        {
            await Send.ResultAsync(PaymentProblems.Create(400, PaymentProblems.MissingPayload,
                "Missing encrypted payload.", "encryptedPayload is required."));
            return;
        }

        var errors = ConfirmPaymentRequestRules.Validate(request);
        if (errors.Count > 0)
        {
            await Send.ResultAsync(PaymentProblems.Create(400, PaymentProblems.InvalidRequest,
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
                PayloadDecryptionError.DecryptionFailed => PaymentProblems.Create(400, PaymentProblems.DecryptionFailed,
                    "The encrypted payload could not be decrypted.",
                    "It was not encrypted for the configured Payload Encryption key, or it was modified."),
                PayloadDecryptionError.InvalidPayload => PaymentProblems.Create(422, PaymentProblems.InvalidPaymentData,
                    "Invalid decrypted payment data.",
                    "The decrypted payload is not a supported tokenized payment (token and cryptogram are required)."),
                _ => PaymentProblems.Create(400, PaymentProblems.InvalidPayload, "Invalid encrypted payload.",
                    "Provide a five-part compact JWE using RSA-OAEP-256 / A128CBC-HS256.")
            });
        }
        catch (PayloadKeyUnavailableException)
        {
            logger.LogWarning("Order {OrderId}: the payload decryption certificate cannot be loaded.", orderId);
            await Send.ResultAsync(PaymentProblems.Create(503, PaymentProblems.DecryptionUnavailable,
                "Payload decryption is temporarily unavailable."));
        }
        catch (InvalidPaymentDataException ex)
        {
            logger.LogWarning("Order {OrderId}: decrypted payment data rejected ({ErrorCount} rule(s)).",
                orderId, ex.Errors.Count);
            await Send.ResultAsync(PaymentProblems.Create(422, PaymentProblems.InvalidPaymentData,
                "Invalid decrypted payment data.", ex.Message, ex.Errors));
        }
        catch (PaymentProcessingException ex)
        {
            logger.LogWarning("Order {OrderId}: payment processing failed: {Reason}", orderId, ex.Message);
            await Send.ResultAsync(PaymentProblems.Create(502, PaymentProblems.PaymentProcessingFailed,
                "Payment processing failed.", ex.Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Only the type: messages of unexpected exceptions are not guaranteed to be free of payment data.
            logger.LogError("Order {OrderId}: unexpected {ExceptionType}.", orderId, ex.GetType().FullName);
            await Send.ResultAsync(PaymentProblems.Create(500, PaymentProblems.UnexpectedError,
                "An unexpected error occurred.", $"Reference the order id {orderId} when reporting it."));
        }
    }
}
