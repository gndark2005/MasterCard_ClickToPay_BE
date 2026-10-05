using System.Security.Cryptography;

namespace MC_ClickToPay.Services.Abstractions;

/// <summary>Resolves the OAuth 1.0a signing key of the Mastercard Developers project.</summary>
public interface IMastercardSigningKeyProvider
{
    ValueTask<RSA> GetSigningKeyAsync(CancellationToken cancellationToken = default);
}
