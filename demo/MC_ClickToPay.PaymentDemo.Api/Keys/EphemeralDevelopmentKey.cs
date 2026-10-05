using System.Security.Cryptography;

namespace MC_ClickToPay.PaymentDemo.Api.Keys;

/// <summary>A 2048-bit RSA key that lives only in memory for the lifetime of the process (local testing only).</summary>
public sealed class EphemeralDevelopmentKey : IDisposable
{
    private readonly Lazy<RSA> key = new(() => RSA.Create(2048));

    /// <summary>Returns a new RSA instance owned by the caller.</summary>
    public RSA Create(bool includePrivateParameters)
    {
        var copy = RSA.Create();
        copy.ImportParameters(key.Value.ExportParameters(includePrivateParameters));
        return copy;
    }

    public void Dispose()
    {
        if (key.IsValueCreated)
        {
            key.Value.Dispose();
        }
    }
}
