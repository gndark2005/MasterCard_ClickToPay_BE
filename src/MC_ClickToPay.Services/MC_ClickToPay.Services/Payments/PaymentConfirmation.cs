namespace MC_ClickToPay.Services.Payments;

public sealed class PaymentConfirmation
{
    public required PaymentRequest Request { get; init; }

    public required PaymentResult Result { get; init; }

    public required string Processor { get; init; }

    public required bool Simulated { get; init; }
}
