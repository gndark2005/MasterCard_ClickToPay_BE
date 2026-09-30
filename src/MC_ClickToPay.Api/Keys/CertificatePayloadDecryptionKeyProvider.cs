using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MC_ClickToPay.Services.Abstractions;
using Microsoft.Extensions.Options;

namespace MC_ClickToPay.Api.Keys;

public sealed class CertificatePayloadDecryptionKeyProvider(IOptions<PayloadEncryptionOptions> options)
    : IPayloadDecryptionKeyProvider
{
    public ValueTask<RSA> GetPrivateKeyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.CertificatePath))
        {
            throw new PayloadKeyUnavailableException();
        }

        try
        {
            using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
                settings.CertificatePath, settings.CertificatePassword,
                X509KeyStorageFlags.EphemeralKeySet);
            var key = certificate.GetRSAPrivateKey();
            if (key is null)
            {
                throw new PayloadKeyUnavailableException();
            }

            if (key.KeySize < 2048)
            {
                key.Dispose();
                throw new PayloadKeyUnavailableException();
            }

            return ValueTask.FromResult(key);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Do not propagate certificate paths, passwords or crypto diagnostics to HTTP/logging.
            throw new PayloadKeyUnavailableException();
        }
    }
}
