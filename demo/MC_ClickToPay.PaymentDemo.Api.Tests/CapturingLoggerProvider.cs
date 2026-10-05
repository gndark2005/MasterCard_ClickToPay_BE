using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace MC_ClickToPay.PaymentDemo.Api.Tests;

/// <summary>Keeps every formatted log line so tests can check that no payment data is logged.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> messages = new();

    public IReadOnlyCollection<string> Messages => messages.ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(messages);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            messages.Enqueue(exception is null ? formatter(state, null) : $"{formatter(state, exception)} {exception}");
    }
}
