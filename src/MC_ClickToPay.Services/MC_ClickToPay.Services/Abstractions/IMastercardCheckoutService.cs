using MC_ClickToPay.Services.Checkout;

namespace MC_ClickToPay.Services.Abstractions;

public interface IMastercardCheckoutService
{
    /// <summary>Calls POST /srci/api/checkout and decrypts the returned encryptedPayload.</summary>
    Task<CompleteCheckoutResult> CompleteAsync(CompleteCheckoutRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Calls POST /srci/api/checkout/confirmations with the payment result (Mastercard answers 204).
    /// Throws <see cref="Exceptions.MastercardCheckoutException"/> with Mastercard's status code when it rejects it.
    /// </summary>
    Task<CheckoutConfirmationResult> ConfirmAsync(CheckoutConfirmationRequest request, CancellationToken cancellationToken = default);
}
