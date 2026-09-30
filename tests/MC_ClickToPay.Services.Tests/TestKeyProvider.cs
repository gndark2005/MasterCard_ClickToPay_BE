using System.Security.Cryptography;
using MC_ClickToPay.Services.Abstractions;

namespace MC_ClickToPay.Services.Tests;

internal sealed class TestKeyProvider(RSAParameters parameters) : IPayloadDecryptionKeyProvider
{
    public ValueTask<RSA> GetPrivateKeyAsync(CancellationToken cancellationToken = default)
    {
        var key = RSA.Create();
        key.ImportParameters(parameters);
        return ValueTask.FromResult(key);
    }
}
