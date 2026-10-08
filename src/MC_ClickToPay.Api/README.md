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
| Payload Encryption private key | Decrypting `encryptedPayload` | `PayloadEncryption:CertificatePath` (`.pem`, or `.p12/.pfx` + `CertificatePassword`) |
| OAuth signing key | Signing `/srci/api/checkout` and `/confirmations` | `MastercardApi:SigningKeyPath` (`.pem`, or `.p12/.pfx` + `SigningKeyPassword`) |

Both keys can be used exactly as Mastercard Developers provides them: a PEM private key
(`-----BEGIN RSA PRIVATE KEY-----` or `-----BEGIN PRIVATE KEY-----`), detected by the `.pem` extension, with no
password and no conversion. A `.p12/.pfx` keystore also works. User Secrets are loaded in the `Development`
environment (the `https` launch profile):

```powershell
dotnet user-secrets set "PayloadEncryption:CertificatePath" "$env:USERPROFILE\.mastercard\clicktopay\payload_encryption.pem" --project src/MC_ClickToPay.Api
dotnet user-secrets set "MastercardApi:SigningKeyPath" "$env:USERPROFILE\.mastercard\clicktopay\signing_key.pem" --project src/MC_ClickToPay.Api
dotnet user-secrets set "MastercardApi:ConsumerKey" "<clientId!keyId>" --project src/MC_ClickToPay.Api
```

Environment variables work too (`PayloadEncryption__CertificatePath`, ...). Check what is set with
`dotnet user-secrets list --project src/MC_ClickToPay.Api`.

Alternatively, put the same keys in `src/MC_ClickToPay.Api/appsettings.Local.json` (same JSON shape as
`appsettings.json`). It is git-ignored, loaded only in `Development` and overrides `appsettings.json`, User Secrets
and environment variables. The payment demo supports the same file.

## Required settings

Only payload decryption (`POST /api/payloads/decrypt`) is in use for now, so only its settings are required. The API
validates them at startup and refuses to start, naming each missing key (`Configuration/RequiredSettings.cs`):

| Key | What it is |
|---|---|
| `Authentication:ApiKey` | `X-Api-Key` expected from callers. |
| `PayloadEncryption:CertificatePath` | Payload Encryption private key: `.pem` as Mastercard provides it, or `.p12/.pfx` (+ `CertificatePassword` if it has one). |

Needed by `POST /api/checkout` (it returns HTTP 502/503 without them): `MastercardApi:BaseUrl`, `SrcDpaId`,
`ConsumerKey` (`clientId!keyId`), `SigningKeyPath` (`.pem`, or `.p12/.pfx` with `SigningKeyPassword`).

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

## Checkout

`POST /api/checkout` receives what the front gets from Click to Pay `checkoutWithCard()`, calls Mastercard
`POST /srci/api/checkout` (OAuth 1.0a), decrypts the `encryptedPayload` and returns the decrypted body, **always with a
`card` object**. There is no PowerTranz call here: with the clear card data, the UI calls PowerTranz SPI itself.

```
UI: init() -> getCards()/authenticate() -> checkoutWithCard()
  -> POST /api/checkout { spiToken, srcDpaId, srcCorrelationId, merchantTransactionId, flowId, xCorrelationId }
     -> Mastercard POST /srci/api/checkout -> encryptedPayload + assuranceData.eci
     -> decrypt (Payload Encryption private key) -> model by dynamicData.dynamicDataType
  <- { credentialType, dynamicDataType, eci, payload (with card) }
UI: PowerTranz /api/spi/... with the clear card data
```

Headers: `Content-Type: application/json`, `X-Api-Key: <Authentication:ApiKey>`.

| Field | Required | Source |
|---|---|---|
| `spiToken` | yes, max 2048 | PowerTranz SPI token of the UI's payment. Not used by this API (traceability only) and never logged. |
| `srcDpaId` | yes | Must equal `MastercardApi:SrcDpaId`, otherwise 400. |
| `srcCorrelationId` | yes | `checkoutWithCard()`: `checkoutResponseData.srcCorrelationId` |
| `merchantTransactionId` | yes | `checkoutWithCard()`: `headers["merchant-transaction-id"]` |
| `flowId` | yes | `checkoutWithCard()`: `headers["x-src-cx-flow-id"]` |
| `xCorrelationId` | yes, max 256, visible ASCII | The UI's tracing id. Logged and returned in the `X-Correlation-Id` response header; not sent to Mastercard. |

All ids are single use and expire a few minutes after `checkoutWithCard()`.

```json
{
  "spiToken": "<PowerTranz SpiToken>",
  "srcDpaId": "823ef281-1a2e-4204-a69c-43d355522a35",
  "srcCorrelationId": "34f4a04b.2ce61515-55b7-4c09-845b-45492c2ef327",
  "merchantTransactionId": "0a4e0d3.34f4a04b.f91587770b3c99ce1090fe8a4f2a931b10d7acfc",
  "flowId": "34f4a04b.2ce61515-55b7-4c09-845b-45492c2ef327.1790888627",
  "xCorrelationId": "7d1f6c2e-9a43-4b8e-b1a2-3f5c8e0d4a17"
}
```

`200 OK` (DSRP + PAN payload):

```json
{
  "merchantTransactionId": "0a4e0d3.34f4a04b.f91587770b3c99ce1090fe8a4f2a931b10d7acfc",
  "correlationId": "34f4a04b.2ce61515-55b7-4c09-845b-45492c2ef327",
  "eci": "06",
  "credentialType": "Pan",
  "dynamicDataType": "CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM",
  "payload": {
    "card": { "primaryAccountNumber": "5120350100064537", "panExpirationMonth": "07", "panExpirationYear": "2029", "cardholderFullName": "John Doe" },
    "token": { "paymentToken": "************9541", "tokenExpirationMonth": "08", "tokenExpirationYear": "2027" },
    "dynamicData": { "dynamicDataValue": "AH14E2rQmy6mABQkMkPpAAADFA==", "dynamicDataType": "CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM" },
    "billingAddress": { "line1": "150 5th Avenue", "city": "New York", "state": "NY", "countryCode": "US", "zip": "10011" },
    "consumerEmailAddress": "john.doe@mastercard.com",
    "consumerMobileNumber": { "countryCode": "44", "phoneNumber": "7966778607" }
  }
}
```

**Decrypted payload formats.** `dynamicData.dynamicDataType` is what we request in `paymentOptions`
(`init()`, `checkoutWithCard()` and `/srci/api/checkout`; today `CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM`); whether the
card (PAN) comes depends on how Mastercard configured the DPA (FPAN / dual payload).

| Format | Decrypted payload | `dynamicDataType` | `credentialType` / `payload.card` |
|---|---|---|---|
| FPAN | `card` only | `NONE` | `Pan` / the PAN |
| DSRP + PAN | `card` (PAN) + masked `token` + cryptogram | `CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM` | `Pan` / the PAN |
| PAN + DTVC | `card` + dynamic security code | `DYNAMIC_CARD_SECURITY_CODE` | `Pan` / the PAN |
| Token only | `token` + cryptogram | `CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM` | `NetworkToken` / filled from the token (not the real PAN) |

The agreed formats are FPAN and DSRP + PAN. `dynamicData.dynamicDataType` decides the model the payload is
deserialized into (`DecryptedPayloadJsonConverter`): `TokenizedPayloadDto`, `DsrpPanPayloadDto` (same type, with a
`card`), `DynamicSecurityCodePayloadDto` or `FpanPayloadDto`. Common fields (addresses, consumer, `dynamicData`) live
in the base `DecryptedPayloadDto`, `card` in `CardPayloadDto`; the response model is `CheckoutPanPayloadDto`.

**The response carries full card data** (PAN or token, cryptogram/DTVC): it is meant for the UI's PowerTranz SPI
request only, is never logged, and must not be cached or stored by the caller.

| Status | When |
|---|---|
| 400 | Invalid request (missing fields, `srcDpaId` of another DPA) or invalid encrypted payload. |
| 401 | `X-Api-Key` missing or wrong. |
| 502 | Mastercard rejected the call or could not be reached (e.g. expired ids, invalid signature); its reason code is included. |
| 503 | Signing key or Payload Encryption key not configured or unreadable. |

The payment flow (validation, PowerTranz/simulated processor) stays in `MC_ClickToPay.Services/Payments` for the
upcoming confirmation endpoint; `IMastercardCheckoutService.ConfirmAsync` (Mastercard `/checkout/confirmations`) too.
## Tests

```powershell
dotnet test src/MC_ClickToPay.Services.slnx
```

HTTP tests use a temporary certificate and verify decryption, the checkout response for every payload format, API-key access, error handling
and Swagger. A real Mastercard sandbox `encryptedPayload` (DPA `823ef281-...`) was decrypted and confirmed locally
on 2026-10-05 with the project's Payload Encryption key configured through User Secrets.
