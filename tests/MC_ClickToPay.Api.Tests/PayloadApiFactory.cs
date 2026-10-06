using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MC_ClickToPay.Api.Keys;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MC_ClickToPay.Api.Tests;

public sealed class PayloadApiFactory : WebApplicationFactory<Program>
{
    public const string ApiKey = "integration-test-only-api-key";

    public RSA EncryptionKey { get; } = RSA.Create(2048);

    private readonly string certificatePath = Path.Combine(Path.GetTempPath(), $"mctp-test-{Guid.NewGuid():N}.pfx");

    private readonly string pemPath = Path.Combine(Path.GetTempPath(), $"mctp-test-{Guid.NewGuid():N}.pem");

    public bool MissingCertificate { get; set; }

    /// <summary>Configure the key as a PKCS#1 PEM ("BEGIN RSA PRIVATE KEY"), as Mastercard Developers provides it.</summary>
    public bool UsePemKey { get; set; }

    public PayloadApiFactory()
    {
        var request = new CertificateRequest("CN=Local Test", EncryptionKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        File.WriteAllBytes(certificatePath, certificate.Export(X509ContentType.Pfx, "test-password"));
        File.WriteAllText(pemPath, EncryptionKey.ExportRSAPrivateKeyPem());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // No Mastercard API settings: decryption must work without them.
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Authentication:ApiKey"] = ApiKey,
                ["MastercardApi:ConsumerKey"] = "",
                ["MastercardApi:SigningKeyPath"] = "",
            }));
        builder.ConfigureServices(services => services.PostConfigure<PayloadEncryptionOptions>(options =>
        {
            // A configured path whose file is missing: startup passes and decryption returns HTTP 503.
            options.CertificatePath = MissingCertificate
                ? Path.Combine(Path.GetTempPath(), $"mctp-missing-{Guid.NewGuid():N}.pfx")
                : UsePemKey ? pemPath : certificatePath;
            options.CertificatePassword = UsePemKey ? null : "test-password";
        }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            EncryptionKey.Dispose();
            File.Delete(certificatePath);
            File.Delete(pemPath);
        }
    }
}
