using Microsoft.Extensions.Options;

namespace MC_ClickToPay.PaymentDemo.Api.Tests;

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal sealed class TestOptions<T>(T value) : IOptionsSnapshot<T>, IOptions<T>
    where T : class
{
    public T Value => value;

    public T Get(string? name) => value;
}
