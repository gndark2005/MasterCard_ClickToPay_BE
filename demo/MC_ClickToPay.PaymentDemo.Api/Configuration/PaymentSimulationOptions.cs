namespace MC_ClickToPay.PaymentDemo.Api.Configuration;

public sealed class PaymentSimulationOptions
{
    /// <summary>Read on every request, so a change in appsettings.json applies without restarting.</summary>
    public SimulatedOutcome Outcome { get; set; } = SimulatedOutcome.Approved;
}
