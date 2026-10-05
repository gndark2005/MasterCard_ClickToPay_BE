using MC_ClickToPay.Services.Abstractions;
using MC_ClickToPay.Services.Models;

namespace MC_ClickToPay.PaymentDemo.Api.Payments;

/// <summary>
/// Encrypted payload -> decrypt (existing <see cref="IPayloadDecryptionService"/>) -> validate/map -> processor.
/// Logs only the order id, amount, outcome and token last four digits.
/// </summary>
public sealed class PaymentConfirmationService(
    IPayloadDecryptionService decryption,
    PaymentRequestFactory requestFactory,
    IPaymentProcessor processor,
    ILogger<PaymentConfirmationService> logger)
{
    /// <exception cref="MC_ClickToPay.Services.Exceptions.PayloadDecryptionException">Invalid, undecryptable or unsupported payload.</exception>
    /// <exception cref="Keys.DemoKeyUnavailableException">No decryption key is configured.</exception>
    /// <exception cref="InvalidPaymentDataException">The decrypted data cannot be paid with.</exception>
    /// <exception cref="PaymentProcessingException">The processor could not give a result.</exception>
    public async Task<PaymentConfirmation> ConfirmAsync(
        string encryptedPayload, decimal amount, string currencyCode, string orderId, CancellationToken cancellationToken)
    {
        var payload = await decryption.DecryptAsync(
            new DecryptPayloadRequest { EncryptedPayload = encryptedPayload }, cancellationToken);
        var request = requestFactory.Create(payload, amount, currencyCode, orderId);
        logger.LogInformation(
            "Order {OrderId}: payload decrypted (token ending {TokenLast4}); sending {Amount} {Currency} to the {Processor} processor.",
            orderId, request.TokenLast4, request.Amount, request.CurrencyCode, processor.Name);

        var result = await processor.ProcessAsync(request, cancellationToken);
        logger.LogInformation("Order {OrderId}: {Processor} processor returned {Outcome} ({ResponseCode}).",
            orderId, processor.Name, result.Approved ? "Approved" : "Declined", result.ResponseCode);

        return new PaymentConfirmation
        {
            Request = request,
            Result = result,
            Processor = processor.Name,
            Simulated = processor.IsSimulated
        };
    }
}
