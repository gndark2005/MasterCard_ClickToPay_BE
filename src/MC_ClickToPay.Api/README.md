# Mastercard payload decryption development API

Temporary .NET 10 API using FastEndpoints. Each class has its own file.

## Run and test with Swagger

1. Open `appsettings.json`. The development API key is already configured:
   `Authentication:ApiKey = clicktopay-dev-key`.
2. Set `PayloadEncryption:CertificatePath` to the absolute path of your Payload
   Encryption `.p12/.pfx` and set `CertificatePassword` if required.
3. Run:

```powershell
dotnet run --project src/MC_ClickToPay.Api --launch-profile https
```

4. Open https://localhost:7180/swagger, select **Authorize**, and enter
   `clicktopay-dev-key` without a Bearer prefix.
5. Use **Try it out** on `POST /api/payloads/decrypt` and replace the sample
   `encryptedPayload` with a real five-part JWE.

Swagger documents the request, response models and status codes in English.
The OpenAPI document is available at `/swagger/v1/swagger.json`.
An alternative request is included in `MC_ClickToPay.Api.http`.

The API key is stored as plain text in appsettings for developer testing.
There is no API-key encryption or User Secrets configuration.
The key is independent of Mastercard credentials and the payload encryption key.
The HTTPS launch profile requires a trusted ASP.NET Core development certificate.

## Payload decryption

The API reuses `IPayloadDecryptionService`. It returns `DecryptedPayloadDto`
containing the token, expiration, cryptogram and optional consumer/address data.
The input is `encryptedPayload`, not the full Checkout JWS. Verification of the
outer JWS signature remains the caller's responsibility.

`CertificatePayloadDecryptionKeyProvider` loads the private RSA key from the
configured certificate. This must match the public Payload Encryption key
registered with Mastercard. No extra HTTP service is needed.

Without a configured certificate, a supported JWE receives HTTP 503.
Missing or incorrect API keys receive HTTP 401.
Request validation and decryption errors receive HTTP 400.

## Tests

```powershell
dotnet test src/MC_ClickToPay.Services.slnx
```

HTTP tests use a temporary certificate and verify decryption, API-key access,
error handling and Swagger. Testing with a real Mastercard sandbox payload
and its matching key remains pending.
