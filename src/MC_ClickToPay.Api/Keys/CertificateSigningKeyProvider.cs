using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MC_ClickToPay.Services.Abstractions;
using Microsoft.Extensions.Options;

namespace MC_ClickToPay.Api.Keys;

public sealed class CertificateSigningKeyProvider(IOptions<MastercardSigningKeyOptions> options)
    : IMastercardSigningKeyProvider
{
    public ValueTask<RSA> GetSigningKeyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.SigningKeyPath))
        {
            throw new SigningKeyUnavailableException();
        }

        try
        {
            // Mastercard Developers can hand out the signing key as a PEM private key or as a PKCS#12 keystore.
            if (Path.GetExtension(settings.SigningKeyPath).Equals(".pem", StringComparison.OrdinalIgnoreCase))
            {
                var key = RSA.Create();
                try
                {
                    key.ImportFromPem(File.ReadAllText(settings.SigningKeyPath));
                    return ValueTask.FromResult(key);
                }
                catch
                {
                    key.Dispose();
                    throw;
                }
            }

            using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
                settings.SigningKeyPath, settings.SigningKeyPassword,
                X509KeyStorageFlags.EphemeralKeySet);
            return ValueTask.FromResult(certificate.GetRSAPrivateKey() ?? throw new SigningKeyUnavailableException());
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Do not propagate key paths, passwords or crypto diagnostics to HTTP/logging.
            throw new SigningKeyUnavailableException();
        }
    }
}

public sealed class SigningKeyUnavailableException()
    : Exception("The Mastercard OAuth signing key is not configured or could not be loaded.");
