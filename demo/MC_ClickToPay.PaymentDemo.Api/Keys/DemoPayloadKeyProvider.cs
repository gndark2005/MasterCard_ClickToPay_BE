using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MC_ClickToPay.Services.Abstractions;
using Microsoft.Extensions.Options;

namespace MC_ClickToPay.PaymentDemo.Api.Keys;

/// <summary>
/// Resolves the Payload Encryption key: the configured certificate (same loading rules as
/// MC_ClickToPay.Api's CertificatePayloadDecryptionKeyProvider) or, for local testing, the ephemeral key.
/// </summary>
public sealed class DemoPayloadKeyProvider(IOptions<DemoPayloadEncryptionOptions> options, EphemeralDevelopmentKey ephemeralKey)
    : IPayloadDecryptionKeyProvider
{
    public const int MinimumKeySize = 2048;

    public ValueTask<RSA> GetPrivateKeyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Load(includePrivateKey: true));
    }

    /// <summary>The public half of the same key, used only by the sample payload endpoint to encrypt test data.</summary>
    public RSA GetPublicKey() => Load(includePrivateKey: false);

    private RSA Load(bool includePrivateKey)
    {
        var settings = options.Value;
        if (!string.IsNullOrWhiteSpace(settings.CertificatePath))
        {
            return LoadFromCertificate(settings, includePrivateKey);
        }

        if (settings.UseEphemeralDevelopmentKey)
        {
            return ephemeralKey.Create(includePrivateKey);
        }

        throw new DemoKeyUnavailableException();
    }

    private static RSA LoadFromCertificate(DemoPayloadEncryptionOptions settings, bool includePrivateKey)
    {
        try
        {
            using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
                settings.CertificatePath, settings.CertificatePassword, X509KeyStorageFlags.EphemeralKeySet);
            var key = includePrivateKey ? certificate.GetRSAPrivateKey() : certificate.GetRSAPublicKey();
            if (key is null)
            {
                throw new DemoKeyUnavailableException();
            }

            if (key.KeySize < MinimumKeySize)
            {
                key.Dispose();
                throw new DemoKeyUnavailableException();
            }

            return key;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Do not propagate certificate paths, passwords or crypto diagnostics to HTTP/logging.
            throw new DemoKeyUnavailableException();
        }
    }
}
