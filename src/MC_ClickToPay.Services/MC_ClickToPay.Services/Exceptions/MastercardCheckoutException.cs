namespace MC_ClickToPay.Services.Exceptions;

/// <summary>The Mastercard API rejected the call or could not be reached. Messages never include payload data.</summary>
public sealed class MastercardCheckoutException(string message, int? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public int? StatusCode { get; } = statusCode;
}
