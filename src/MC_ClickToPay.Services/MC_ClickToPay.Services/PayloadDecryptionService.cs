using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using Jose;
using MC_ClickToPay.Services.Abstractions;
using MC_ClickToPay.Services.Exceptions;
using MC_ClickToPay.Services.Models;

namespace MC_ClickToPay.Services;

public sealed class PayloadDecryptionService(IPayloadDecryptionKeyProvider keyProvider) : IPayloadDecryptionService
{
    // Initial profile matches Mastercard's sample. Extend only after confirming the real profile.
    public const int MaximumPayloadLength = 128 * 1024;

    public async Task<DecryptedPayloadDto> DecryptAsync(
        DecryptPayloadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var encrypted = request.EncryptedPayload;
        AssertValidEncryptedPayload(encrypted);

        // Provider errors intentionally remain operational errors, not invalid-client-payload errors.
        using var key = await keyProvider.GetPrivateKeyAsync(cancellationToken);
        AssertValidDecryptionKey(key);
        cancellationToken.ThrowIfCancellationRequested();

        string json;

        try
        {
            json = JWT.Decode(encrypted, key, JweAlgorithm.RSA_OAEP_256, JweEncryption.A128CBC_HS256);
        }
        catch (Exception ex) when (ex is JoseException or CryptographicException or ArgumentException or FormatException)
        {
            throw new PayloadDecryptionException(PayloadDecryptionError.DecryptionFailed);
        }

        try
        {
            var payload = JsonSerializer.Deserialize<DecryptedPayloadDto>(json);
            AssertValidDecryptedPayload(payload);

            return payload;
        }
        catch (JsonException)
        {
            throw new PayloadDecryptionException(PayloadDecryptionError.InvalidPayload);
        }
    }

    private static void AssertValidEncryptedPayload(string encrypted)
    {
        if (string.IsNullOrWhiteSpace(encrypted) || encrypted.Length > MaximumPayloadLength)
        {
            throw new PayloadDecryptionException(PayloadDecryptionError.InvalidInput);
        }

        var parts = encrypted.Split('.');

        if (parts.Length != 5 || parts.Any(p => p.Length == 0 ||
            p.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))))
        {
            throw new PayloadDecryptionException(PayloadDecryptionError.InvalidInput);
        }

        try
        {
            using var header = JsonDocument.Parse(Base64Url.Decode(parts[0]));
            var root = header.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() > 1) ||
                !root.TryGetProperty("alg", out var alg) || alg.GetString() != "RSA-OAEP-256" ||
                !root.TryGetProperty("enc", out var enc) || enc.GetString() != "A128CBC-HS256" ||
                root.TryGetProperty("zip", out _) || root.TryGetProperty("crit", out _))
            {
                throw new PayloadDecryptionException(PayloadDecryptionError.UnsupportedHeader);
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            throw new PayloadDecryptionException(PayloadDecryptionError.InvalidInput);
        }
    }

    private static void AssertValidDecryptionKey([NotNull] RSA? key)
    {
        if (key is null || key.KeySize < 2048)
        {
            throw new InvalidOperationException("A private RSA Payload Encryption key of at least 2048 bits is required.");
        }
    }

    private static void AssertValidDecryptedPayload([NotNull] DecryptedPayloadDto? payload)
    {
        // The model was already chosen from dynamicData.dynamicDataType (DecryptedPayloadJsonConverter);
        // each one needs its credential, and all but FPAN need the dynamic value.
        var hasDynamicValue = !string.IsNullOrWhiteSpace(payload?.DynamicData?.DynamicDataValue);
        var valid = payload switch
        {
            TokenizedPayloadDto tokenized => !string.IsNullOrWhiteSpace(tokenized.Token?.PaymentToken) && hasDynamicValue,
            DsrpPanPayloadDto or DynamicSecurityCodePayloadDto =>
                !string.IsNullOrWhiteSpace(((CardPayloadDto)payload).Card?.PrimaryAccountNumber) && hasDynamicValue,
            FpanPayloadDto fpan => !string.IsNullOrWhiteSpace(fpan.Card?.PrimaryAccountNumber),
            _ => false
        };

        if (payload is null || !valid)
        {
            throw new PayloadDecryptionException(PayloadDecryptionError.InvalidPayload);
        }
    }
}
