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
            // Mastercard Developers hands out the Payload Encryption private key as a PEM; a .p12/.pfx also works.
            var key = Path.GetExtension(settings.CertificatePath).Equals(".pem", StringComparison.OrdinalIgnoreCase)
                ? LoadPem(settings.CertificatePath)
                : LoadPkcs12(settings.CertificatePath, settings.CertificatePassword);

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

    private static RSA LoadPem(string path)
    {
        var key = RSA.Create();
        try
        {
            key.ImportFromPem(File.ReadAllText(path));
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static RSA LoadPkcs12(string path, string? password)
    {
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.EphemeralKeySet);
        return certificate.GetRSAPrivateKey() ?? throw new PayloadKeyUnavailableException();
    }
}
