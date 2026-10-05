namespace MC_ClickToPay.PaymentDemo.Api.Payments;

/// <summary>The processor could not give a result. Messages are returned to clients, so they never include card data.</summary>
public sealed class PaymentProcessingException(string message, Exception? inner = null) : Exception(message, inner);
