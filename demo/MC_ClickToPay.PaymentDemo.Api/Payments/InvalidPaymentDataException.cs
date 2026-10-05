namespace MC_ClickToPay.PaymentDemo.Api.Payments;

/// <summary>The payload decrypted but cannot be paid with. <see cref="Errors"/> name fields and rules, never values.</summary>
public sealed class InvalidPaymentDataException(IReadOnlyList<string> errors)
    : Exception("The decrypted payment data is invalid.")
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
