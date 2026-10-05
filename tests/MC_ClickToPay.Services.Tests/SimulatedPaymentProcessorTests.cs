using MC_ClickToPay.Services.Payments;
using Xunit;

namespace MC_ClickToPay.Services.Tests;

public sealed class SimulatedPaymentProcessorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ApprovesByDefault()
    {
        var result = await Processor(new PaymentSimulationOptions()).ProcessAsync(Request(), CancellationToken.None);

        Assert.True(result.Approved);
        Assert.Equal("00", result.ResponseCode);
        Assert.Matches("^[0-9]{6}$", result.AuthorizationCode);
        Assert.Equal(Now, result.ProcessedAt);
    }

    [Fact]
    public async Task DeclinesWhenConfigured()
    {
        var options = new PaymentSimulationOptions { Outcome = SimulatedOutcome.Declined };

        var result = await Processor(options).ProcessAsync(Request(), CancellationToken.None);

        Assert.False(result.Approved);
        Assert.Equal("05", result.ResponseCode);
        Assert.Null(result.AuthorizationCode);
    }

    [Fact]
    public async Task FailsWhenConfigured()
    {
        var options = new PaymentSimulationOptions { Outcome = SimulatedOutcome.Failure };

        await Assert.ThrowsAsync<PaymentProcessingException>(
            () => Processor(options).ProcessAsync(Request(), CancellationToken.None));
    }

    private static SimulatedPaymentProcessor Processor(PaymentSimulationOptions options) =>
        new(new TestOptions<PaymentSimulationOptions>(options), new FixedTimeProvider(Now));

    private static PaymentRequest Request() => new()
    {
        OrderId = "ORDER-1",
        Amount = 6m,
        CurrencyCode = "USD",
        NetworkToken = "5480983179133165",
        TokenExpiration = "3007",
        Cryptogram = "cryptogram",
        CryptogramType = PaymentRequestFactory.SupportedCryptogramType
    };
}
