using FastEndpoints;
using MC_ClickToPay.Api.Keys;
using MC_ClickToPay.Services.Abstractions;
using MC_ClickToPay.Services.Exceptions;
using MC_ClickToPay.Services.Models;

namespace MC_ClickToPay.Api.Endpoints.Payloads;

public sealed class DecryptPayloadEndpoint(IPayloadDecryptionService decryption)
    : Endpoint<DecryptPayloadRequest, DecryptedPayloadDto>
{
    public override void Configure()
    {
        Post("/api/payloads/decrypt");
        Summary(s =>
        {
            s.Summary = "Decrypt a Mastercard transaction payload";
            s.Description = "Accepts only encryptedPayload, a five-part compact JWE extracted from a Checkout response " +
                "whose signature has already been verified. " +
                "Supported profile: RSA-OAEP-256 / A128CBC-HS256. Returns tokenized DSRP payment data.";
            s.ExampleRequest = new DecryptPayloadRequest
            {
                EncryptedPayload = "<header>.<encrypted-key>.<iv>.<ciphertext>.<authentication-tag>"
            };
            s.RequestParam(r => r.EncryptedPayload, "The encryptedPayload JWE. Replace the placeholder with a real value; do not submit the full JWS.");
            s.Response<DecryptedPayloadDto>(200, "Decrypted payload: payment token, expiration, cryptogram and optional data.");
            s.Response(400, "Invalid input, unsupported profile or decryption failure. Validation uses the FastEndpoints format; decryption errors use Problem Details.");
            s.Response(401, "API key is missing, incorrect or not configured.");
            s.Response(413, "Request body exceeds the 256 KiB limit.");
            s.Response(503, "Decryption certificate is not configured or unavailable.");
        });
    }

    public override async Task HandleAsync(DecryptPayloadRequest request, CancellationToken ct)
    {
        try
        {
            var result = await decryption.DecryptAsync(request, ct);
            await Send.OkAsync(result, ct);
        }
        catch (PayloadDecryptionException)
        {
            await Send.ResultAsync(Results.Problem(statusCode: 400,
                title: "Invalid encrypted payload.",
                detail: "Provide a valid encryptedPayload JWE using the supported encryption profile."));
        }
        catch (PayloadKeyUnavailableException)
        {
            await Send.ResultAsync(Results.Problem(statusCode: 503,
                title: "Payload decryption is temporarily unavailable."));
        }
    }
}
