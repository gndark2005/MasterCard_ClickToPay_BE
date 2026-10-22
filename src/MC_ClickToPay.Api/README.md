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
| `spiToken` | yes, max 2048 | SPI token generated by the PowerTranz HPP for this payment and sent by the HPP. Written to the daily transaction file: `POST /api/confirmations/{spiToken}` finds the transaction by it. Never logged. |
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

The payment flow (validation, PowerTranz/simulated processor) stays in `MC_ClickToPay.Services/Payments`.

## Confirmation: POST /api/confirmations/{spiToken}

Reports the outcome of a payment to Mastercard `POST /srci/api/checkout/confirmations` (OAuth 1.0a, same credentials
as `/api/checkout`). Called by the external system **after the PowerTranz payment** (`/spi/Payment`): the `SpiToken` of
the payment response goes in the route, and its result in the body. Every other value comes from the daily
transaction file row with that `spitoken`, written by `/api/checkout` (files searched newest first; see
[ctp-transaction-file.md](../../docs/ctp-transaction-file.md)).

```bash
curl -k -X POST https://localhost:7180/api/confirmations/<spiToken> -H "X-Api-Key: clicktopay-dev-key" \
  -H "Content-Type: application/json" \
  -d '{"approved":true,"authorizationCode":"123456","responseMessage":"Transaction is approved","networkTransactionIdentifier":"mastercard"}'
```

| Body field | Required | From PowerTranz | Sent to Mastercard as |
|---|---|---|---|
| `approved` | yes | `Approved` | `checkoutEventType` 01; `checkoutEventStatus` and `confirmationStatus` 01 (true) or 02 (false) |
| `authorizationCode` | **yes when `approved` is true**; up to 25 letters/digits | `AuthorizationCode` | `networkAuthorizationCode` |
| `responseMessage` | no | `ResponseMessage` | `confirmationReason`: characters Mastercard does not accept are dropped and it is cut to 64 |
| `networkTransactionIdentifier` | no; up to 25 of `A-Z a-z 0-9 space _ -` | (see below) | `networkTransactionIdentifier`; **`UNAVLB` when not sent** |

What is sent is written to the transaction file row (`confirmation_reason`, `network_authorization_code`,
`network_transaction_identifier` and the codes), also when Mastercard rejects the confirmation.

**`networkTransactionIdentifier`.** Mastercard (`checkout1.0.yaml`, `ConfirmationData`): *"The unique authorisation
related tracing value assigned by a Payment Network and provided in an authorisation response. Required only when
checkoutEventType=01. If checkoutEventType=01 and the value of networkTransactionIdentifier is unknown, please pass
UNAVLB."* The confirmation always sends `checkoutEventType` 01, so the API sends `UNAVLB` when the caller has no value.
**Open question:** which PowerTranz `/spi/Payment` response field (if any) carries that network value, so the caller can
send it. Ask PowerTranz.

Verified against the Mastercard sandbox on 2026-10-20 with a real Click to Pay checkout and a PowerTranz approved
payment: without `networkAuthorizationCode` and `networkTransactionIdentifier` Mastercard answers
`400 [INVALID_ARGUMENT] One or more fields in the request are either missing or does not have correct value`; with
them (`123456`, `mastercard`) it accepts the confirmation. Per the spec the missing required one is
`networkTransactionIdentifier`, hence the `UNAVLB` default.

1. Finds the row of the SPI token.
2. Sends `SrcCorrelationId` (body `correlationId`), `MerchantTransactionId` (body and `merchant-transaction-id`
   header), `FlowId` (`X-Src-Cx-Flow-Id` header), the amount and the outcome. `confirmationTimestamp` is now (UTC).
3. Updates the transaction: the row's outcome columns, `confirmation_result` (`Confirmed` / `ConfirmationFailed`),
   `confirmation_date`, `confirmation_time` and `confirmation_detail` (Mastercard's reason on failure), and the
   database state.

Step-by-step test requests (checkout, confirmation, 409, 404, 400, 401) are in `MC_ClickToPay.Api.http`. With
`MastercardApi:Simulate = true` (Development only) neither call reaches Mastercard.

A `Confirmed` row is not sent again (409); a `ConfirmationFailed` row can be confirmed again. `200 OK`:

```json
{
  "status": "Confirmed",
  "fileName": "ctp_txn_20261015.csv",
  "line": 2,
  "correlationId": "34f4a04b.2ce61515-55b7-4c09-845b-45492c2ef327",
  "xCorrelationId": "7d1f6c2e-9a43-4b8e-b1a2-3f5c8e0d4a17",
  "flowId": "34f4a04b.2ce61515-55b7-4c09-845b-45492c2ef327.1790888627"
}
```

| Status | When |
|---|---|
| 200 | Mastercard accepted it. Headers `X-Src-Cx-Flow-Id` (the flow id Mastercard returned) and `X-Correlation-Id` (the row's). |
| 400 | SPI token longer than 2048; body without `approved`, approved without `authorizationCode`, or a value with the wrong format; or Mastercard rejected the data (its reason code is in `detail`). |
| 401 | `X-Api-Key` missing or wrong. |
| 404 | No row has this SPI token (`Transaction not found.`), or Mastercard did not find the checkout transaction. |
| 409 | Already confirmed, or another request is confirming the same SPI token. |
| 422 | The row's data is invalid (amount, currency, ids); `detail` says which. |
| 502 | Mastercard unreachable, 5xx, or it refused the integration credentials / DPA (401/403). |
| 503 | Signing key not configured or unreadable, or the transaction files cannot be read. |

The SPI token is never logged or returned. It travels in the URL, so keep request-path logging (proxies, IIS logs,
`Microsoft.AspNetCore` at Information) off for this route.

Verified against the Mastercard sandbox on 2026-10-15: the signed call reaches `/checkout/confirmations` and an
unknown `correlationId` returns `404 [NOT_FOUND] Transaction data not found`.

### Checkout state database (MySQL)

With `ConnectionStrings:ClickToPay` set, every checkout is recorded in MySQL (`checkout_transactions`) as `Pending`,
and every confirmation moves it to `Confirmed` or `ConfirmationFailed`. The table is created at
startup; the database never fails a request. Setup, local portable MySQL and queries:
[docs/db/README.md](../../docs/db/README.md).

### Daily transaction file (source of the confirmations)

Mastercard `/checkout/confirmations` is called outside the checkout, by `POST /api/confirmations/{spiToken}`, which
reads the transaction from a daily CSV. Every successful `POST /api/checkout` (HTTP 200) appends one row to it,
**after** the response has been sent, so the file never delays or fails the checkout. Failed checkouts are not
written; a row that cannot be written is logged as an error with its `srcCorrelationId` and `xCorrelationId`.

- Folder: `CheckoutTransactionFile:Directory` (default `transactions`, relative to the content root).
- File: `ctp_txn_YYYYMMDD.csv`, one per day (server local time), with a header row; every checkout of the day adds a
  row. The six outcome columns and the last four are written empty; the confirmation fills them in.

```text
date,time,spitoken,transaction_amount,transaction_currency,SrcDpaId,SrcCorrelationId,MerchantTransactionId,FlowId,XCorrelationId,checkout_event_type,checkout_event_status,confirmation_status,confirmation_reason,network_authorization_code,network_transaction_identifier,confirmation_result,confirmation_date,confirmation_time,confirmation_detail
20261012,090507,<spiToken>,31.25,USD,823ef281-...,34f4a04b.2ce61515-...,0a4e0d3.34f4a04b...,34f4a04b...1790888627,7d1f6c2e-...,,,,,,,,,,
```

`date` is `YYYYMMDD`, `time` `HHMMSS`; values with a comma or quote are quoted (RFC 4180). No card data is written,
but the file holds SPI tokens: restrict access to the folder.

## Logging

Two places, both on by default:

| Where | What | Setting |
|---|---|---|
| File `api-YYYYMMDD.log` | Everything the API logs (Information and up): each `/api/*` request with route, status, duration, correlation ids and error; Mastercard calls; startup, migrations, database errors. One file per day, the last 30 kept. | `FileLogging:Directory` (default `logs`, relative to the API folder; on the server `C:\clicktopay\logs`), `FileLogging:RetainedDays` |
| MySQL `api_request_log` | One row per `/api/*` request, successful or not, with its error. Only when `ConnectionStrings:ClickToPay` is set. | Migration `0002`; see [docs/db/README.md](../../docs/db/README.md#request-log-api_request_log) |

- The file log is the one to read when the API does not start (settings, keys, a failed migration) or MySQL is down:
  those errors cannot reach the database. Under IIS the console is discarded, so the file is the only record.
- The IIS app pool identity needs write access to the log folder.
- Never logged: card data, cryptograms, request/response bodies, SPI tokens (requests are logged with the route
  template, and `Microsoft.AspNetCore` stays at Warning because its own request logs carry the path).
- Levels can be changed in the `Serilog` section, e.g. `"Serilog": { "MinimumLevel": { "Default": "Warning" } }`.
- Example line: `[WRN] ApiRequestLogMiddleware: POST /api/confirmations/{spiToken} -> 409 in 21 ms (srcCorrelationId
  34f4a04b..., x-correlation-id ...): Transaction not confirmed again. The transaction was already confirmed to Mastercard.`

## Tests

```powershell
dotnet test src/MC_ClickToPay.Services.slnx
```

HTTP tests use a temporary certificate and verify decryption, the checkout response for every payload format, API-key access, error handling
and Swagger. A real Mastercard sandbox `encryptedPayload` (DPA `823ef281-...`) was decrypted and confirmed locally
on 2026-10-05 with the project's Payload Encryption key configured through User Secrets.
