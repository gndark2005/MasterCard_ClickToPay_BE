namespace MC_ClickToPay.Services.Payments;

/// <summary>
/// The step after decryption. <see cref="SimulatedPaymentProcessor"/> is registered today; a PowerTranz
/// implementation replaces it once a test card can complete the Mastercard -> PowerTranz flow.
/// </summary>
public interface IPaymentProcessor
{
    /// <summary>Name returned to clients, e.g. "Simulated" or "PowerTranz".</summary>
    string Name { get; }

    bool IsSimulated { get; }

    /// <summary>
    /// Returns the result for approvals and declines. Throws <see cref="PaymentProcessingException"/> when the
    /// processor cannot give a result (unreachable, rejected request, timeout).
    /// </summary>
    Task<PaymentResult> ProcessAsync(PaymentRequest request, CancellationToken cancellationToken);
}
