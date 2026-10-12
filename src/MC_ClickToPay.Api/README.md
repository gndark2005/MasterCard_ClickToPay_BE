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

Needed by `POST /api/checkout` (it returns HTTP 502/503 without them): `MastercardApi:BaseUrl`,
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

`POST /api/checkout` receives what the UI gets from Click to Pay `checkoutWithCard()`, calls Mastercard
`POST /srci/api/checkout` (OAuth 1.0a), decrypts the `encryptedPayload` and returns the card and addresses as **flat
fields** for the UI's PowerTranz SPI request. There is no PowerTranz call here.

```
UI: init() -> getCards()/authenticate() -> checkoutWithCard()
  -> POST /api/checkout { spiToken, srcDpaId, srcCorrelationId, merchantTransactionId, flowId,
                          transactionAmount, transactionCurrencyCode, xCorrelationId }
     -> Mastercard POST /srci/api/checkout -> encryptedPayload
     -> decrypt (Payload Encryption private key) -> model by dynamicData.dynamicDataType
  <- { cardNo, cardExpDate, cardCVV2, cardholderName, billTo*, shipTo* }
UI: PowerTranz /api/spi/... with the non-null fields
```

Headers: `Content-Type: application/json`, `X-Api-Key: <Authentication:ApiKey>`.

| Field | Required | Source |
|---|---|---|
| `spiToken` | yes, max 2048 | PowerTranz SPI token of the UI's payment. Not used by this API (traceability only) and never logged. |
| `srcDpaId` | yes, max 256 | The merchant's DPA (varies per merchant); sent as is to Mastercard `/checkout`. Mastercard is expected to reject a DPA outside our integrator credentials (502; not yet verified). |
| `srcCorrelationId` | yes | `checkoutWithCard()`: `checkoutResponseData.srcCorrelationId` |
| `merchantTransactionId` | yes | `checkoutWithCard()`: `headers["merchant-transaction-id"]` |
| `flowId` | yes | `checkoutWithCard()`: `headers["x-src-cx-flow-id"]` |
| `transactionAmount` | yes, > 0, max 2 decimals | Order amount, the same one given to `checkoutWithCard()`. Sent to Mastercard `/checkout`. |
| `transactionCurrencyCode` | yes | ISO 4217 alphabetic code, 3 uppercase letters (e.g. `USD`). Sent to Mastercard `/checkout`. |
| `xCorrelationId` | yes, max 256, visible ASCII | The UI's tracing id. Logged and returned in the `X-Correlation-Id` response header; not sent to Mastercard. |

All ids are single use and expire a few minutes after `checkoutWithCard()`.

```json
{
  "spiToken": "<PowerTranz SpiToken>",
  "srcDpaId": "823ef281-1a2e-4204-a69c-43d355522a35",
  "srcCorrelationId": "34f4a04b.2ce61515-55b7-4c09-845b-45492c2ef327",
  "merchantTransactionId": "0a4e0d3.34f4a04b.f91587770b3c99ce1090fe8a4f2a931b10d7acfc",
  "flowId": "34f4a04b.2ce61515-55b7-4c09-845b-45492c2ef327.1790888627",
  "transactionAmount": 31.25,
  "transactionCurrencyCode": "USD",
  "xCorrelationId": "7d1f6c2e-9a43-4b8e-b1a2-3f5c8e0d4a17"
}
```

`200 OK` (DSRP + PAN payload; header `X-Correlation-Id` = the request's `xCorrelationId`). `cardNo` and `cardExpDate`
are always set; every other field is `null` when Mastercard did not send it, and the UI drops null fields before
calling PowerTranz.

```json
{
  "cardNo": "5120350100064537",
  "cardExpDate": "2907",
  "cardCVV2": null,
  "cardholderName": "John Doe",
  "billToFirstName": "John",
  "billToLastName": "Doe",
  "billToAddress1": "150 5th Avenue",
  "billToAddress2": null,
  "billToState": "NY",
  "billToPostCode": "10011",
  "billToCountry": "US",
  "billToTelephone": "12125550100",
  "billToEmail": "john.doe@example.com",
  "shipToFirstName": "John",
  "shipToLastName": "Doe",
  "shipToAddress1": "150 5th Avenue",
  "shipToAddress2": null,
  "shipToState": "NY",
  "shipToPostCode": "10011",
  "shipToCountry": "US",
  "shipToTelephone": "12125550100",
  "shipToEmail": "john.doe@example.com"
}
```

| Field | Source in the decrypted payload |
|---|---|
| `cardNo` | `card.primaryAccountNumber`; the network token (`token.paymentToken`) when Mastercard sent only a token |
| `cardExpDate` | YYMM from the card (or token) expiry month/year |
| `cardCVV2` | `dynamicData.dynamicDataValue` only when `dynamicDataType` is `DYNAMIC_CARD_SECURITY_CODE` |
| `cardholderName` | `card.cardholderFullName`, else the token's, else the consumer's name |
| `billTo/shipToFirstName`, `LastName` | Address `name` split at the last space; else `consumerFirstName` / `consumerLastName` |
| `billTo/shipToAddress1`, `Address2`, `State`, `PostCode`, `Country` | Address `line1`, `line2`, `state`, `zip`, `countryCode` (ISO alpha-2) |
| `billTo/shipToTelephone` | `consumerMobileNumber`: country code + number, digits only (shipTo only when there is a shipping address) |
| `billTo/shipToEmail` | `consumerEmailAddress` (shipTo only when there is a shipping address) |

The address `city` is not in the agreed response. The cryptogram, ECI and token are not returned either.
A payload without a card number or expiry gets 502.
**Decrypted payload formats.** `dynamicData.dynamicDataType` is what we request in `paymentOptions`
(`init()`, `checkoutWithCard()` and `/srci/api/checkout`; today `CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM`); whether the
card (PAN) comes depends on how Mastercard configured the DPA (FPAN / dual payload).

| Format | Decrypted payload | `dynamicDataType` | `cardNo` / `cardCVV2` |
|---|---|---|---|
| FPAN | `card` only | `NONE` | the PAN / null |
| DSRP + PAN | `card` (PAN) + masked `token` + cryptogram | `CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM` | the PAN / null |
| PAN + DTVC | `card` + dynamic security code | `DYNAMIC_CARD_SECURITY_CODE` | the PAN / the dynamic code |
| Token only | `token` + cryptogram | `CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM` | the token number (not the real PAN) / null |

The agreed formats are FPAN and DSRP + PAN. `dynamicData.dynamicDataType` decides the model the payload is
deserialized into (`DecryptedPayloadJsonConverter`): `TokenizedPayloadDto`, `DsrpPanPayloadDto` (same type, with a
`card`), `DynamicSecurityCodePayloadDto` or `FpanPayloadDto`. Common fields (addresses, consumer, `dynamicData`) live
in the base `DecryptedPayloadDto`, `card` in `CardPayloadDto`; `CheckoutPanPayloadDto` fills `card` from the token when needed and `CompleteCheckoutResponse` flattens it.

**The response carries full card data** (PAN or token, DTVC): it is meant for the UI's PowerTranz SPI
request only, is never logged, and must not be cached or stored by the caller.

| Status | When |
|---|---|
| 400 | Invalid request (missing or invalid fields) or invalid encrypted payload. |
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
