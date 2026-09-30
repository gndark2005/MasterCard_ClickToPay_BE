using System.Security.Cryptography;
using MC_ClickToPay.Services.Abstractions;

namespace MC_ClickToPay.Services.Tests;

internal sealed class UnavailableKeyProvider : IPayloadDecryptionKeyProvider
{
    public ValueTask<RSA> GetPrivateKeyAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Key provider must not be called.");
}
