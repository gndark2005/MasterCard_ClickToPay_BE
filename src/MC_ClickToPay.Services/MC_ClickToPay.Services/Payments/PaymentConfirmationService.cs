using MC_ClickToPay.Services.Abstractions;
using MC_ClickToPay.Services.Models;
using Microsoft.Extensions.Logging;

namespace MC_ClickToPay.Services.Payments;

/// <summary>
/// Encrypted payload -> decrypt (<see cref="IPayloadDecryptionService"/>) -> validate/map -> processor.
/// Logs only the order id, amount, outcome and token last four digits.
/// </summary>
public sealed class PaymentConfirmationService(
    IPayloadDecryptionService decryption,
    PaymentRequestFactory requestFactory,
    IPaymentProcessor processor,
    ILogger<PaymentConfirmationService> logger)
{
    /// <exception cref="Exceptions.PayloadDecryptionException">Invalid, undecryptable or unsupported payload.</exception>
    /// <exception cref="InvalidPaymentDataException">The decrypted data cannot be paid with.</exception>
    /// <exception cref="PaymentProcessingException">The processor could not give a result.</exception>
    /// <remarks>The key provider's own exception propagates when no decryption key is available.</remarks>
    public async Task<PaymentConfirmation> ConfirmAsync(ConfirmPaymentRequest request, string orderId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payload = await decryption.DecryptAsync(
            new DecryptPayloadRequest { EncryptedPayload = request.EncryptedPayload! }, cancellationToken);
        var paymentRequest = requestFactory.Create(payload, request.TransactionAmount!.Value,
            request.TransactionCurrencyCode!, orderId, request.Eci);
        logger.LogInformation(
            "Order {OrderId}: payload decrypted (token ending {TokenLast4}); sending {Amount} {Currency} to the {Processor} processor.",
            orderId, paymentRequest.TokenLast4, paymentRequest.Amount, paymentRequest.CurrencyCode, processor.Name);

        var result = await processor.ProcessAsync(paymentRequest, cancellationToken);
        logger.LogInformation("Order {OrderId}: {Processor} processor returned {Outcome} ({ResponseCode}).",
            orderId, processor.Name, result.Approved ? "Approved" : "Declined", result.ResponseCode);

        return new PaymentConfirmation
        {
            Request = paymentRequest,
            Result = result,
            Processor = processor.Name,
            Simulated = processor.IsSimulated
        };
    }
}
