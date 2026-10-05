namespace MC_ClickToPay.PaymentDemo.Api.Keys;

/// <summary>No usable key is configured. The message never includes paths, passwords or crypto diagnostics.</summary>
public sealed class DemoKeyUnavailableException()
    : Exception("The payload encryption key is unavailable.");
