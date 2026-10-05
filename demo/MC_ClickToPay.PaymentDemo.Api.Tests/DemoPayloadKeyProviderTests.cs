using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Jose;
using MC_ClickToPay.PaymentDemo.Api.Keys;
using MC_ClickToPay.Services;
using MC_ClickToPay.Services.Models;
using Xunit;

namespace MC_ClickToPay.PaymentDemo.Api.Tests;

public sealed class DemoPayloadKeyProviderTests : IDisposable
{
    private const string Password = "test-password";

    private readonly string certificatePath = Path.Combine(Path.GetTempPath(), $"mctp-demo-test-{Guid.NewGuid():N}.pfx");

    private readonly EphemeralDevelopmentKey ephemeralKey = new();

    public DemoPayloadKeyProviderTests()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Payment Demo Test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        File.WriteAllBytes(certificatePath, certificate.Export(X509ContentType.Pfx, Password));
    }

    [Fact]
    public async Task CertificateKeyDecryptsWhatItsPublicKeyEncrypts()
    {
        var provider = Provider(new DemoPayloadEncryptionOptions { CertificatePath = certificatePath, CertificatePassword = Password });

        var payload = await RoundTripAsync(provider);

        Assert.Equal("5480983179133165", payload.Token!.PaymentToken);
    }

    [Fact]
    public async Task EphemeralKeyDecryptsWhatItsPublicKeyEncrypts()
    {
        var provider = Provider(new DemoPayloadEncryptionOptions { UseEphemeralDevelopmentKey = true });

        var payload = await RoundTripAsync(provider);

        Assert.Equal("5480983179133165", payload.Token!.PaymentToken);
    }

    [Fact]
    public async Task CertificateTakesPrecedenceOverEphemeralKey()
    {
        var both = Provider(new DemoPayloadEncryptionOptions
        {
            CertificatePath = certificatePath,
            CertificatePassword = Password,
            UseEphemeralDevelopmentKey = true
        });
        var ephemeralOnly = Provider(new DemoPayloadEncryptionOptions { UseEphemeralDevelopmentKey = true });

        using var certificateKey = both.GetPublicKey();
        using var developmentKey = ephemeralOnly.GetPublicKey();

        Assert.NotEqual(certificateKey.ExportParameters(false).Modulus, developmentKey.ExportParameters(false).Modulus);
        await RoundTripAsync(both);
    }

    [Fact]
    public async Task NothingConfiguredIsUnavailable()
    {
        var provider = Provider(new DemoPayloadEncryptionOptions());

        await Assert.ThrowsAsync<DemoKeyUnavailableException>(() => provider.GetPrivateKeyAsync().AsTask());
        Assert.Throws<DemoKeyUnavailableException>(() => provider.GetPublicKey());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnreadableCertificateIsUnavailableWithoutLeakingPathOrPassword(bool wrongPassword)
    {
        var options = new DemoPayloadEncryptionOptions
        {
            CertificatePath = wrongPassword ? certificatePath : certificatePath + ".missing",
            CertificatePassword = wrongPassword ? "wrong-password" : Password
        };

        var ex = await Assert.ThrowsAsync<DemoKeyUnavailableException>(() => Provider(options).GetPrivateKeyAsync().AsTask());

        Assert.DoesNotContain(certificatePath, ex.Message);
        Assert.DoesNotContain("password", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        ephemeralKey.Dispose();
        File.Delete(certificatePath);
    }

    private DemoPayloadKeyProvider Provider(DemoPayloadEncryptionOptions options) =>
        new(new TestOptions<DemoPayloadEncryptionOptions>(options), ephemeralKey);

    private static async Task<DecryptedPayloadDto> RoundTripAsync(DemoPayloadKeyProvider provider)
    {
        using var publicKey = provider.GetPublicKey();
        var encrypted = JWT.Encode("""
            {"token":{"paymentToken":"5480983179133165"},
             "dynamicData":{"dynamicDataValue":"c","dynamicDataType":"CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM"}}
            """, publicKey, JweAlgorithm.RSA_OAEP_256, JweEncryption.A128CBC_HS256);
        return await new PayloadDecryptionService(provider).DecryptAsync(new DecryptPayloadRequest { EncryptedPayload = encrypted });
    }
}
