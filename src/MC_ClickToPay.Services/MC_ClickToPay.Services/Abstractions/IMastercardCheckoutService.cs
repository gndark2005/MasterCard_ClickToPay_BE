using MC_ClickToPay.Services.Checkout;

namespace MC_ClickToPay.Services.Abstractions;

public interface IMastercardCheckoutService
{
    /// <summary>Calls POST /srci/api/checkout and decrypts the returned encryptedPayload.</summary>
    Task<CompleteCheckoutResult> CompleteAsync(CompleteCheckoutRequest request, CancellationToken cancellationToken = default);

    /// <summary>Calls POST /srci/api/checkout/confirmations with the payment result.</summary>
    Task ConfirmAsync(CheckoutConfirmationRequest request, CancellationToken cancellationToken = default);
}
