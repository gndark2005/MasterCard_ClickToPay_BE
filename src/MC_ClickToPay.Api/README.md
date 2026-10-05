# Mastercard payload decryption development API

Temporary .NET 10 API using FastEndpoints. Each class has its own file.

## Run and test with Swagger

1. Open `appsettings.json`. The development API key is already configured:
   `Authentication:ApiKey = clicktopay-dev-key`.
2. Set the local keys with User Secrets (see "Local keys with User Secrets"). Never
   put key paths or passwords in `appsettings.json`.
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
The key is independent of Mastercard credentials and the payload encryption key.
The HTTPS launch profile requires a trusted ASP.NET Core development certificate.

## Local keys with User Secrets

Mastercard Developers gives the project two different RSA keys. Keep both outside the repository (for example in
`%USERPROFILE%\.mastercard\clicktopay\`):

| Key | Used for | Setting |
|---|---|---|
| Payload Encryption private key | Decrypting `encryptedPayload` | `PayloadEncryption:CertificatePath` (`.p12/.pfx`) + `CertificatePassword` |
| OAuth signing key | Signing `/srci/api/checkout` and `/confirmations` | `MastercardApi:SigningKeyPath` (`.pem`, or `.p12/.pfx` + `SigningKeyPassword`) |

The decryption provider loads a `.p12/.pfx`. If Mastercard gave you the encryption key as a PEM, wrap it in a
`.p12` (a self-signed certificate around the same key is enough). User Secrets are loaded in the `Development`
environment (the `https` launch profile):

```powershell
dotnet user-secrets set "PayloadEncryption:CertificatePath" "$env:USERPROFILE\.mastercard\clicktopay\payload_encryption.p12" --project src/MC_ClickToPay.Api
dotnet user-secrets set "PayloadEncryption:CertificatePassword" "<p12 password>" --project src/MC_ClickToPay.Api
dotnet user-secrets set "MastercardApi:SigningKeyPath" "$env:USERPROFILE\.mastercard\clicktopay\signing_key.pem" --project src/MC_ClickToPay.Api
dotnet user-secrets set "MastercardApi:ConsumerKey" "<clientId!keyId>" --project src/MC_ClickToPay.Api
```

Environment variables work too (`PayloadEncryption__CertificatePath`, ...). Check what is set with
`dotnet user-secrets list --project src/MC_ClickToPay.Api`.

## Required settings

Only payload decryption (`POST /api/payloads/decrypt`) is in use for now, so only its settings are required. The API
validates them at startup and refuses to start, naming each missing key (`Configuration/RequiredSettings.cs`):

| Key | What it is |
|---|---|
| `Authentication:ApiKey` | `X-Api-Key` expected from callers. |
| `PayloadEncryption:CertificatePath` | `.p12/.pfx` with the Payload Encryption private key. `CertificatePassword` if it has one. |

Optional until the full Mastercard → PowerTranz flow is used (`/api/checkout/complete` and `/confirmations` return
HTTP 502/503 without them): `MastercardApi:BaseUrl`, `SrcDpaId`, `ConsumerKey` (`clientId!keyId`),
`SigningKeyPath` (`.pem`, or `.p12/.pfx` with `SigningKeyPassword`).

A configured file that cannot be read (wrong path or password) still starts the API and returns HTTP 503 on use,
without exposing the path or password.

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

## Payment confirmation

`POST /api/payments/confirm` takes the `encryptedPayload` returned by Mastercard `/checkout`, decrypts it with the
Payload Encryption key, validates and maps the payment data, and hands it to the payment processor.

```
checkoutWithCard() (browser) -> POST /api/checkout/complete -> encryptedPayload + eci
  -> POST /api/payments/confirm -> decrypt -> validate/map -> IPaymentProcessor -> confirmation
  -> POST /api/checkout/confirmations (report the result to Mastercard)
```

The processor is **simulated** (`SimulatedPaymentProcessor`) until a test card can complete the Mastercard ->
PowerTranz flow: no money moves and every response has `"simulated": true`. Its outcome comes from
`PaymentSimulation:Outcome` (`Approved`, `Declined` or `Failure`), read on every request.

The flow lives in `MC_ClickToPay.Services/Payments` and is shared with the payment demo
(`demo/MC_ClickToPay.PaymentDemo.Api`). PowerTranz plugs in as another `IPaymentProcessor` registered in
`Program.cs`; see the demo README, "Future PowerTranz integration".

Headers: `Content-Type: application/json`, `X-Api-Key: <Authentication:ApiKey>`.

| Field | Required | Rules |
|---|---|---|
| `encryptedPayload` | yes | Five-part compact JWE, RSA-OAEP-256 / A128CBC-HS256 (Mastercard `/checkout` `encryptedPayload`). |
| `transactionAmount` | yes | Greater than 0, at most 1000000, at most 2 decimals. Same amount sent to Mastercard. |
| `transactionCurrencyCode` | yes | ISO 4217: `USD` (Mastercard) or `840` (PowerTranz). |
| `orderId` | no | 1 to 50 letters, digits, `-` or `_`. Generated (`ORD-...`) when omitted. |
| `eci` | no | 2 digits: `assuranceData.eci` of the `/checkout` response (it is not inside the payload). |

```json
{
  "encryptedPayload": "eyJraWQiOiJwYXlsb2FkX2VuY19jZXJ0IiwiZW5jIjoiQTEyOENCQy1IUzI1NiIsImFsZyI6IlJTQS1PQUVQLTI1NiJ9.<key>.<iv>.<ciphertext>.<tag>",
  "transactionAmount": 31.25,
  "transactionCurrencyCode": "USD",
  "orderId": "ORDER-5",
  "eci": "06"
}
```

`200 OK` (real Mastercard sandbox payload, 2026-10-05):

```json
{
  "status": "Approved", "approved": true, "simulated": true, "processor": "Simulated",
  "orderId": "ORDER-5", "transactionId": "5d13b388-211c-4f77-8752-453b7f3d6f1e",
  "authorizationCode": "464006", "responseCode": "00", "responseMessage": "Approved (simulated)",
  "transactionAmount": 31.25, "transactionCurrencyCode": "USD", "tokenLast4": "2671", "eci": "06",
  "processedAt": "2026-10-05T17:15:53.3926605+00:00"
}
```

A decline is also `200` (`approved: false`, `responseCode: "05"`). Errors are Problem Details with a `code`:

| Status | `code` | When |
|---|---|---|
| 400 | `missing_payload` | `encryptedPayload` missing or blank. |
| 400 | `invalid_request` | Amount, currency, order id or eci invalid (`errors` lists the rules). |
| 400 | `invalid_payload` | Not a five-part JWE, or unsupported `alg`/`enc`. |
| 400 | `decryption_failed` | Encrypted for another key, or modified. |
| 401 | | `X-Api-Key` missing or wrong. |
| 422 | `invalid_payment_data` | Decrypted, but token/expiry/cryptogram invalid or expired. |
| 500 | `unexpected_error` | Anything else; details are never returned. |
| 502 | `payment_processing_failed` | The processor could not give a result. |
| 503 | `decryption_unavailable` | The Payload Encryption certificate cannot be loaded. |

Only the token's last four digits are returned or logged.

## Tests

```powershell
dotnet test src/MC_ClickToPay.Services.slnx
```

HTTP tests use a temporary certificate and verify decryption, payment confirmation, API-key access, error handling
and Swagger. A real Mastercard sandbox `encryptedPayload` (DPA `823ef281-...`) was decrypted and confirmed locally
on 2026-10-05 with the project's Payload Encryption key configured through User Secrets.
