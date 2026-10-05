using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace MC_ClickToPay.Services.Payments;

/// <summary>
/// Stands in for PowerTranz until a test card can complete the Mastercard -> PowerTranz flow. Nothing leaves the
/// process; the outcome comes from <see cref="PaymentSimulationOptions.Outcome"/>.
/// </summary>
public sealed class SimulatedPaymentProcessor(IOptionsSnapshot<PaymentSimulationOptions> options, TimeProvider time)
    : IPaymentProcessor
{
    public string Name => "Simulated";

    public bool IsSimulated => true;

    public Task<PaymentResult> ProcessAsync(PaymentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var outcome = options.Value.Outcome;
        if (outcome == SimulatedOutcome.Failure)
        {
            throw new PaymentProcessingException(
                "The simulated payment processor is configured to fail (Simulation Outcome = Failure).");
        }

        var approved = outcome == SimulatedOutcome.Approved;
        return Task.FromResult(new PaymentResult
        {
            Approved = approved,
            TransactionId = Guid.NewGuid().ToString(),
            AuthorizationCode = approved
                ? RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString(CultureInfo.InvariantCulture)
                : null,
            ResponseCode = approved ? "00" : "05",
            ResponseMessage = approved ? "Approved (simulated)" : "Do not honour (simulated decline)",
            ProcessedAt = time.GetUtcNow()
        });
    }
}
