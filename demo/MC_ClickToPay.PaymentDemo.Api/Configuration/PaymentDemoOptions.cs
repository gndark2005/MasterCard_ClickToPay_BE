using MC_ClickToPay.Services.Payments;

namespace MC_ClickToPay.PaymentDemo.Api.Configuration;

/// <summary>The "PaymentDemo" section of appsettings.json.</summary>
public sealed class PaymentDemoOptions
{
    public const string SectionName = "PaymentDemo";

    /// <summary>Enables POST /api/demo/payloads/sample, which encrypts <see cref="TestPayment"/>. Development only.</summary>
    public bool EnableSamplePayloadEndpoint { get; set; }

    /// <summary>The test card/payment data the sample endpoint encrypts. Change it here or with environment variables.</summary>
    public TestPaymentData TestPayment { get; set; } = new();

    /// <summary>Bound separately as <see cref="PaymentSimulationOptions"/> for the shared simulated processor.</summary>
    public PaymentSimulationOptions Simulation { get; set; } = new();
}
