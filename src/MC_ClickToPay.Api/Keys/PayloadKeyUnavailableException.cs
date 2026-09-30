namespace MC_ClickToPay.Api.Keys;

public sealed class PayloadKeyUnavailableException()
    : Exception("The payload decryption key is unavailable.");
