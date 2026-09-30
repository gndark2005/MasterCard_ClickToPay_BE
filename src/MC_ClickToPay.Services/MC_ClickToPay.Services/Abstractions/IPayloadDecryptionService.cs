using MC_ClickToPay.Services.Models;

namespace MC_ClickToPay.Services.Abstractions;

public interface IPayloadDecryptionService
{
    /// <summary>Decrypts the compact JWE encryptedPayload, not the outer Checkout JWS.</summary>
    Task<DecryptedPayloadDto> DecryptAsync(DecryptPayloadRequest request, CancellationToken cancellationToken = default);
}
