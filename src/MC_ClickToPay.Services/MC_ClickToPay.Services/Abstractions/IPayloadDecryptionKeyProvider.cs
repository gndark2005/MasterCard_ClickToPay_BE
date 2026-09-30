using System.Security.Cryptography;

namespace MC_ClickToPay.Services.Abstractions;

/// <summary>Resolves the private Payload Encryption key configured by the consuming API.</summary>
public interface IPayloadDecryptionKeyProvider
{
    /// <summary>
    /// Returns a new RSA instance containing the private key. Ownership passes to the caller,
    /// which disposes it. Do not return a shared instance. The implementation chooses the
    /// trusted key; it must not load arbitrary paths or URLs supplied by a payload header.
    /// </summary>
    ValueTask<RSA> GetPrivateKeyAsync(CancellationToken cancellationToken = default);
}
