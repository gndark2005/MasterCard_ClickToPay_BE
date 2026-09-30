namespace MC_ClickToPay.Services.Exceptions;

/// <summary>Safe error without ciphertext, plaintext, keys or underlying crypto diagnostics.</summary>
public sealed class PayloadDecryptionException(PayloadDecryptionError error)
    : Exception($"Payload decryption failed: {error}.")
{
    public PayloadDecryptionError Error { get; } = error;
}
