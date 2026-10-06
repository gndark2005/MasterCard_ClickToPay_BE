# Payment confirmation demo API

A small, self-contained REST API that simulates the payment confirmation step of
the Click to Pay flow:

```
Client -> encryptedPayload -> REST API -> decrypt -> validate/map -> payment processor -> confirmation
```

It lives in `demo/` and is independent of the rest of the repository:
`MC_ClickToPay.Api` and the existing tests do not reference it.

## Purpose

**What it does**

- Receives a Mastercard Click to Pay `encryptedPayload` (a compact JWE) along with
  the amount and currency.
- Decrypts the payload with the **existing** `PayloadDecryptionService` from
  `MC_ClickToPay.Services`: same profile (RSA-OAEP-256 / A128CBC-HS256), same
  validation, no new crypto code.
- Validates the decrypted payment data: token digits, expiry month/year, not
  expired, and cryptogram present and of the supported type.
- Maps the data to a processor-neutral `PaymentRequest`, using the same field
  choices as the PowerTranz mapping in `demo/PowerTranz3DSecurePoc`
  (`ClickToPayService.Map`).
- Hands it to `IPaymentProcessor` and returns a confirmation.
- The validation, mapping, simulated processor and confirmation service live in
  `src/MC_ClickToPay.Services/.../Payments` and are shared with the real endpoint
  `POST /api/payments/confirm` of `MC_ClickToPay.Api`. This demo only adds its own
  key provider (development key) and the sample payload endpoint.

**What it does NOT do**

- It does not call PowerTranz and no money moves. The processor is
  **simulated**, and every response says `"simulated": true`.
- It does not call Mastercard `/checkout`. That remains the job of
  `MC_ClickToPay.Api` (`POST /api/checkout/complete`).
- It does not verify the outer Checkout JWS signature. As in `MC_ClickToPay.Api`,
  the input is only the `encryptedPayload`.

## How to run

Requires the .NET 10 SDK. From the `MasterCard_ClickToPay_BE` folder:

```powershell
dotnet run --project demo/MC_ClickToPay.PaymentDemo.Api --launch-profile https
```

- API: `https://localhost:7190` (HTTP `http://localhost:5190` redirects to HTTPS)
- Swagger UI: `https://localhost:7190/swagger`. Select **Authorize** and enter
  `payment-demo-dev-key`, without a `Bearer` prefix.

The `https` profile runs in the `Development` environment, which turns on the
in-memory development key and the sample payload endpoint. No certificate is
needed to try the demo.

HTTPS uses the ASP.NET Core development certificate. Trust it once with
`dotnet dev-certs https --trust`, or pass `-k` to curl.

## Configuration

All settings are in `appsettings.json`. `appsettings.Development.json` overrides
them for local runs. Any value can also be set with an environment variable, using
`__` in place of `:` (for example `PaymentDemo__Simulation__Outcome=Declined`).

| Setting | Default | Purpose |
|---|---|---|
| `Authentication:ApiKey` | `payment-demo-dev-key` | Value required in the `X-Api-Key` header. Development key only. |
| `PayloadEncryption:CertificatePath` | empty | Absolute path to the Mastercard **Payload Encryption** private key: the `.pem` as Mastercard provides it, or a `.p12/.pfx`. Required to decrypt real Mastercard payloads. Takes precedence over the development key. |
| `PayloadEncryption:CertificatePassword` | empty | Password of that certificate. |
| `PayloadEncryption:UseEphemeralDevelopmentKey` | `false` (`true` in Development) | Generates an RSA-2048 key in memory at startup. Payloads made with it stop decrypting when the API restarts. |
| `PaymentDemo:EnableSamplePayloadEndpoint` | `false` (`true` in Development) | Enables `POST /api/demo/payloads/sample`. Never enable it outside local development. |
| `PaymentDemo:TestPayment:*` | fake test card | The test card/payment data the sample endpoint encrypts (see below). |
| `PaymentDemo:Simulation:Outcome` | `Approved` | `Approved`, `Declined` or `Failure`. Read on every request, so editing `appsettings.json` takes effect without a restart. |

If neither a certificate nor the development key is configured, the confirm
endpoint returns `503 decryption_unavailable`.

Keep secrets out of the repository. Set the certificate path and password with
environment variables or User Secrets (`dotnet user-secrets`), not in a committed
`appsettings*.json`:

```powershell
dotnet user-secrets set "PayloadEncryption:CertificatePath" "$env:USERPROFILE\.mastercard\clicktopay\payload_encryption.pem" --project demo/MC_ClickToPay.PaymentDemo.Api
```

With the certificate set, the confirm endpoint decrypts real Mastercard payloads
(verified on 2026-10-05 with a sandbox `encryptedPayload`), and the sample
endpoint encrypts with the certificate's public key.

### Encryption and decryption

- **Real Mastercard payloads:** set `PayloadEncryption:CertificatePath` and
  `CertificatePassword` to the Payload Encryption key whose public half is
  registered with Mastercard. This uses the same certificate as
  `MC_ClickToPay.Api`.
- **Local testing:** in Development, the API generates its own key. The sample
  endpoint encrypts test data with that key's public half, exactly as Mastercard
  would, so the confirm endpoint can decrypt it.

### Test card and payment data

The test data is in `PaymentDemo:TestPayment` in `appsettings.json`:

```json
"TestPayment": {
  "PaymentToken": "5480983179133165",
  "TokenExpirationMonth": "12",
  "TokenExpirationYear": "2030",
  "PaymentAccountReference": "DEMO0000000000000000000000001",
  "CardholderFullName": "Jane Demo",
  "Cryptogram": "DEMOcryptogramNOTREAL000000=",
  "CryptogramType": "CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM",
  "ConsumerEmailAddress": "jane.demo@example.com",
  "ConsumerFirstName": "Jane",
  "ConsumerLastName": "Demo",
  "BillingAddress": { "Line1": "123 Demo Street", "City": "New York", "State": "NY", "Zip": "10001", "CountryCode": "US" }
}
```

These values are fake. Do not put real card data here.

There are three ways to change the test card:

1. **Permanently:** edit `PaymentDemo:TestPayment` in `appsettings.json` and
   restart the API.
2. **Per run, without editing files:** use environment variables, for example:
   ```powershell
   $env:PaymentDemo__TestPayment__PaymentToken = "5100000000000008"
   $env:PaymentDemo__TestPayment__TokenExpirationYear = "2031"
   dotnet run --project demo/MC_ClickToPay.PaymentDemo.Api --launch-profile https
   ```
3. **Per payload:** send the fields to change in the body of
   `POST /api/demo/payloads/sample`. Any field set there overrides the
   configuration for that payload only, for example
   `{"tokenExpirationYear": "2020"}` to test an expired card. Field names are the
   camelCase versions of the settings above, except `cryptogram` and
   `cryptogramType`.

## API endpoints

Both endpoints require the `X-Api-Key` header. Request bodies are limited to 256 KiB.

### `POST /api/demo/payments/confirm`

Headers: `Content-Type: application/json`, `X-Api-Key: <Authentication:ApiKey>`

Request body:

| Field | Required | Rules |
|---|---|---|
| `encryptedPayload` | yes | Five-part compact JWE, RSA-OAEP-256 / A128CBC-HS256, at most 131072 characters. |
| `transactionAmount` | yes | Greater than 0, at most 1000000, at most 2 decimals. |
| `transactionCurrencyCode` | yes | ISO 4217: 3 uppercase letters (`USD`, as Mastercard uses) or 3 digits (`840`, as PowerTranz uses). |
| `orderId` | no | 1 to 50 letters, digits, `-` or `_`. Generated (`ORD-...`) when omitted. |
| `eci` | no | 2 digits: `assuranceData.eci` from Mastercard `/checkout` (not inside the payload). Returned as-is in the response. |

Example request:

```json
{
  "encryptedPayload": "eyJhbGciOiJSU0EtT0FFUC0yNTYiLCJlbmMiOiJBMTI4Q0JDLUhTMjU2In0.<encrypted-key>.<iv>.<ciphertext>.<tag>",
  "transactionAmount": 6.00,
  "transactionCurrencyCode": "USD",
  "orderId": "ORDER-1001"
}
```

Success, `200 OK` (captured from a local run):

```json
{
  "status": "Approved",
  "approved": true,
  "simulated": true,
  "processor": "Simulated",
  "orderId": "ORDER-1001",
  "transactionId": "10eb6547-93cb-4103-8347-bba4107fe0a1",
  "authorizationCode": "536832",
  "responseCode": "00",
  "responseMessage": "Approved (simulated)",
  "transactionAmount": 6.00,
  "transactionCurrencyCode": "USD",
  "credentialType": "NetworkToken",
  "last4": "3165",
  "processedAt": "2026-10-05T14:37:39.2449392+00:00"
}
```

A decline is also `200 OK`, with `"status": "Declined"`, `"approved": false`,
`"responseCode": "05"` and `"authorizationCode": null`. The token's last four
digits are the only card data ever returned.

Errors are Problem Details (`application/problem+json`) with a stable `code`.
Validation errors also include an `errors` list that names fields and rules,
never values:

| Status | `code` | When |
|---|---|---|
| 400 | `missing_payload` | `encryptedPayload` is missing, empty or blank. |
| 400 | `invalid_request` | Amount, currency or order id is invalid (see `errors`). |
| 400 | `invalid_payload` | Not a five-part JWE, or an unsupported `alg`/`enc` header. |
| 400 | `decryption_failed` | Encrypted for a different key, or modified after encryption. |
| 401 | (no body) | `X-Api-Key` is missing or wrong. |
| 413 | (no body) | Body larger than 256 KiB. |
| 422 | `invalid_payment_data` | Decrypted, but not a usable tokenized payment: missing token or cryptogram, wrong cryptogram type, invalid token digits or expiry, or expired token. |
| 500 | `unexpected_error` | Anything else. The details are never returned; quote the order id from `detail`. |
| 502 | `payment_processing_failed` | The processor could not give a result. Simulate it with `Simulation:Outcome = Failure`. |
| 503 | `decryption_unavailable` | No usable certificate or development key is configured. |

Example `422` (captured from a local run):

```json
{
  "type": "https://tools.ietf.org/html/rfc4918#section-11.2",
  "title": "Invalid decrypted payment data.",
  "status": 422,
  "detail": "The decrypted payment data is invalid.",
  "code": "invalid_payment_data",
  "errors": ["token is expired (token.tokenExpirationMonth/token.tokenExpirationYear are in the past)."],
  "traceId": "00-cccfe0cb9bf7a369e2a4531cf7f19ff1-292e2fc4417b5e69-00"
}
```

### `POST /api/demo/payloads/sample` (development only)

Returns `{"encryptedPayload": "...", "note": "..."}`: the configured test payment
encrypted with the API's key. Send `{}` to use the configuration as is, or
override individual fields (see "Test card and payment data"). It returns
`404 sample_payloads_disabled` unless `PaymentDemo:EnableSamplePayloadEndpoint`
is `true`.

## How to test locally

Start the API (see "How to run"), then use any of the following clients.

### PowerShell

Windows PowerShell 5.1 requires the trusted development certificate. On
PowerShell 7, you can add `-SkipCertificateCheck` instead.

```powershell
$base = "https://localhost:7190"
$headers = @{ "X-Api-Key" = "payment-demo-dev-key" }

# 1. Encrypted test payload from PaymentDemo:TestPayment
$sample = Invoke-RestMethod -Method Post -Uri "$base/api/demo/payloads/sample" -Headers $headers `
    -ContentType "application/json" -Body "{}"

# 2. Confirm the payment
$body = @{
    encryptedPayload        = $sample.encryptedPayload
    transactionAmount       = 6.00
    transactionCurrencyCode = "USD"
    orderId                 = "ORDER-1001"
} | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "$base/api/demo/payments/confirm" -Headers $headers `
    -ContentType "application/json" -Body $body
```

### cURL (bash, Git Bash, WSL or macOS)

```bash
BASE=https://localhost:7190
KEY=payment-demo-dev-key

# 1. Encrypted test payload
PAYLOAD=$(curl -sk -X POST "$BASE/api/demo/payloads/sample" \
  -H "X-Api-Key: $KEY" -H "Content-Type: application/json" -d '{}' \
  | sed -E 's/.*"encryptedPayload":"([^"]+)".*/\1/')

# 2. Confirm the payment (200)
curl -sk -X POST "$BASE/api/demo/payments/confirm" \
  -H "X-Api-Key: $KEY" -H "Content-Type: application/json" \
  -d "{\"encryptedPayload\":\"$PAYLOAD\",\"transactionAmount\":6.00,\"transactionCurrencyCode\":\"USD\",\"orderId\":\"ORDER-1001\"}"

# Missing payload (400 missing_payload)
curl -sk -X POST "$BASE/api/demo/payments/confirm" \
  -H "X-Api-Key: $KEY" -H "Content-Type: application/json" \
  -d '{"transactionAmount":6.00,"transactionCurrencyCode":"USD"}'

# Expired card (422 invalid_payment_data)
EXPIRED=$(curl -sk -X POST "$BASE/api/demo/payloads/sample" \
  -H "X-Api-Key: $KEY" -H "Content-Type: application/json" -d '{"tokenExpirationYear":"2020"}' \
  | sed -E 's/.*"encryptedPayload":"([^"]+)".*/\1/')
curl -sk -X POST "$BASE/api/demo/payments/confirm" \
  -H "X-Api-Key: $KEY" -H "Content-Type: application/json" \
  -d "{\"encryptedPayload\":\"$EXPIRED\",\"transactionAmount\":6.00,\"transactionCurrencyCode\":\"USD\"}"
```

In Windows PowerShell, call `curl.exe` (not the `curl` alias) and pass JSON
bodies from a file with `--data-binary "@body.json"` to avoid quoting problems.

### Postman

1. Create an environment with `baseUrl = https://localhost:7190` and
   `apiKey = payment-demo-dev-key`. If the development certificate is not
   trusted, turn off *Settings > SSL certificate verification*.
2. Add a request `POST {{baseUrl}}/api/demo/payloads/sample` with header
   `X-Api-Key: {{apiKey}}` and a raw JSON body `{}`. In its **Scripts > Post-response** tab, add:
   ```javascript
   pm.environment.set("encryptedPayload", pm.response.json().encryptedPayload);
   ```
3. Add a request `POST {{baseUrl}}/api/demo/payments/confirm` with the same
   header and this raw JSON body:
   ```json
   {
     "encryptedPayload": "{{encryptedPayload}}",
     "transactionAmount": 6.00,
     "transactionCurrencyCode": "USD",
     "orderId": "ORDER-1001"
   }
   ```
4. Send request 2, then request 3.

### Visual Studio / VS Code REST Client

`MC_ClickToPay.PaymentDemo.Api.http` contains the same flow, plus the
expired-card and missing-payload cases. Requests are chained: the confirm
request reuses the sample response automatically.

### Simulating declines and failures

Set `PaymentDemo:Simulation:Outcome` in `appsettings.json` to `Declined` (200 with
`approved: false`) or `Failure` (502). The change applies to the next request, no
restart needed.

### Automated tests

```powershell
dotnet test demo/MC_ClickToPay.PaymentDemo.Api.Tests
```

## Current limitation

The flow stops before a real PowerTranz payment. We do not yet have a test card
that can complete the Mastercard -> PowerTranz flow, so the processor is
simulated. A `200 Approved` from this API means "decrypted, valid, and the
simulated processor approved it", not that a card was charged.

## Future PowerTranz integration

The real call plugs in behind `IPaymentProcessor`
(`src/MC_ClickToPay.Services/MC_ClickToPay.Services/Payments/IPaymentProcessor.cs`), once, for both
`MC_ClickToPay.Api` and this demo:

1. Add a `PowerTranzPaymentProcessor : IPaymentProcessor` that maps
   `PaymentRequest` to a PowerTranz SPI Sale/Auth. Reuse the existing approach in
   `demo/PowerTranz3DSecurePoc`:
   - `Services/PowerTranzService.cs` for the request, headers and error handling;
   - `Services/ClickToPayService.cs` (`Map`) for the field mapping:
     - `AccountNumber` (network token or PAN, see `CredentialType`) -> `Source.CardPan`;
     - `Expiration` (YYMM) -> `Source.CardExpiration`;
     - no CVV;
     - billing `CountryCode` converted from alpha-2 to numeric;
     - the cryptogram and ECI go under the `Source` field names that the PowerTranz
       SPI docs specify (still to be confirmed; `CryptogramSourceField` /
       `EciSourceField` in the POC).
2. Map PowerTranz `Approved`/`IsoResponseCode`/`AuthorizationCode` to
   `PaymentResult`. Throw `PaymentProcessingException`, with a message free of
   card data, when PowerTranz cannot be reached or rejects the request.
3. In each `Program.cs`, register it in place of the simulated processor
   (`AddPaymentConfirmation()` only adds the simulator when nothing else is registered):
   `builder.Services.AddScoped<IPaymentProcessor, PowerTranzPaymentProcessor>();`.
   Keep PowerTranz credentials in User Secrets or environment variables.
4. Decide how to handle 3-D Secure. If PowerTranz answers with `SP4` and
   `RedirectData`, a single synchronous REST call is no longer enough: the API
   will need a redirect step or a callback.
5. Use `PaymentRequest.Eci`: the confirm request already accepts the `eci` from
   `POST /api/checkout/complete` (it is not inside the encrypted payload).

No change to the endpoint, the decryption, or the response contract is needed for
this. Only `"processor"` and `"simulated"` change.
