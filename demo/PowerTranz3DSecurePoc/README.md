# PowerTranz 3DS + Click to Pay demo

Local demo of a small flower shop ("Bloom Bouquets") that pays through **PowerTranz SPI** with 3-D Secure, either
with a card typed by the shopper or with **Mastercard Click to Pay**. It is a .NET 10 console app that drives a real
Chromium window with Playwright, so the 3DS JavaScript, the challenge and the redirects run exactly as in a browser.

## Components

| Component | Role |
|---|---|
| `PowerTranz3DSecurePoc` (this app) | Shop UI (cart → checkout), calls PowerTranz SPI, shows an API console in the page. |
| `MasterCard_ClickToPay_BE/src/MC_ClickToPay.Api` | Our API. Calls Mastercard `/srci/api/checkout` (OAuth 1.0a) and **decrypts the `encryptedPayload`**. |
| PowerTranz staging (`https://staging.ptranz.com`) | `Api/spi/Auth` or `Api/spi/Sale`, `Api/spi/Conductor` (3DS), `Api/spi/Payment`. |
| Mastercard sandbox | Unified Checkout SDK in the browser, `/srci/api/checkout` and `/checkout/confirmations` on the server. |

## How a card payment works

1. **Cart → Check out.** The browser only sends product ids and quantities; the total is priced server-side from
   `Shop:Products`.
2. **STEP 1 — `Api/spi/Auth` (or `Sale`).** Card, amount and `MerchantResponseUrl` are sent with the PowerTranz
   credentials. The expected answer is `SP4 – SPI Preprocessing complete` with `RedirectData` and `SpiToken`.
   `Approved=false` here is normal: nothing has been charged.
3. **STEP 2 — 3-D Secure.** `RedirectData` is loaded in Chromium. Its own JavaScript collects the browser info and posts
   to `Api/spi/Conductor`; the issuer challenge (if any) is completed by hand. When PowerTranz posts the result to
   `MerchantResponseUrl`, Playwright intercepts it (no merchant server needed) and reads the `Response` field.
   `3D0` = authenticated; `3D1`/`SP1` = card not enrolled (payment continues without 3DS).
4. **STEP 3 — `Api/spi/Payment`.** The body is the `SpiToken` as a JSON string. This is the call that authorizes and
   charges; `IsoResponseCode 00` = approved.

## How a Click to Pay payment works

> **Current status:** no test card can yet go from Mastercard all the way to PowerTranz, so the end-to-end flow below
> is not exercised. What is in use today is our API receiving an `encryptedPayload` (`POST /api/payloads/decrypt`)
> and returning it decrypted.

1. The checkout page loads the Mastercard **Unified Checkout SDK** (`ClickToPay:SdkUrl`) with `SrcDpaId` / `DpaName`.
   The shopper signs in by email (OTP) and picks a card; `checkoutWithCard()` returns a `srcCorrelationId` and a
   `merchant-transaction-id`. No card data reaches the merchant at this point.
2. **STEP 0 — `POST {ClickToPay:ApiBaseUrl}/api/checkout/complete`** (header `X-Api-Key`). Our API:
   - calls Mastercard `POST /srci/api/checkout`, signed with OAuth 1.0a (`ConsumerKey` + signing key);
   - receives the `encryptedPayload` (JWE) and **decrypts it** with the Payload Encryption private key;
   - returns the network token + expiry, the cryptogram (`CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM`), the ECI
     (`assuranceData.eci`), cardholder name, email and billing address.
3. The app sends the usual PowerTranz flow (steps 1–3 above) with the **network token as `CardPan`**, the token expiry,
   no CVV, and the cryptogram and ECI in `Source` under `ClickToPay:CryptogramSourceField` / `EciSourceField`.
4. **`POST /api/checkout/confirmations`** reports the result (approved or not) back to Mastercard. A failed
   confirmation is logged and never changes the payment result.

## Required settings

Both apps refuse to start when a required value is missing and list every missing key.

**`PowerTranz3DSecurePoc/appsettings.json`**

| Key | What it is |
|---|---|
| `PowerTranz:BaseUrl` | `https://staging.ptranz.com` |
| `PowerTranz:PowerTranzId`, `PowerTranzPassword` | PowerTranz merchant credentials. |
| `PowerTranz:CurrencyCode` | ISO numeric (`978` = EUR). |
| `PowerTranz:MerchantResponseUrl` | Absolute URL PowerTranz posts the 3DS result to (intercepted locally). |
| `PowerTranz:SpiTransaction` | `Auth` or `Sale`. |
| `PowerTranz:HostedPage:PageSet` + `PageName` | Optional; if one is set, both are required. |
| `ClickToPay:ApiBaseUrl`, `ApiKey` | Our API (`https://localhost:7180`) and its `X-Api-Key`. *(when `Enabled`)* |
| `ClickToPay:SrcDpaId`, `DpaName` | Merchant DPA registered with Mastercard. *(when `Enabled`)* |

Optional for now: `ClickToPay:CryptogramSourceField` / `EciSourceField`, the PowerTranz `Source` field names for the
cryptogram and ECI. They are only needed once a Click to Pay card can go all the way to PowerTranz; until then the
network token is sent as `CardPan` and the console shows a warning.

In headless mode (`Browser:Headless=true`) the shop UI is skipped, so `TotalAmount` and `Card:*` become required.

**`MC_ClickToPay.Api/appsettings.json`**

For now only `POST /api/payloads/decrypt` is used (receive `encryptedPayload`, return it decrypted), so only these
are required:

| Key | What it is |
|---|---|
| `Authentication:ApiKey` | Same value as `ClickToPay:ApiKey` above. |
| `PayloadEncryption:CertificatePath` (+ `CertificatePassword`) | `.p12/.pfx` with the **Payload Encryption private key** whose public key is registered in the Mastercard project. Used to decrypt `encryptedPayload`. |

Optional until the full flow is used: `MastercardApi:BaseUrl`, `SrcDpaId`, `ConsumerKey` (`clientId!keyId`) and
`SigningKeyPath` (+ `SigningKeyPassword` for `.p12`), needed by `/api/checkout/complete` and `/confirmations`.

## Run

```powershell
# 1. Our Click to Pay API (only needed for Click to Pay)
dotnet run --project MasterCard_ClickToPay_BE/src/MC_ClickToPay.Api --launch-profile https

# 2. The demo (first time only: install Chromium)
dotnet run --project PowerTranz3DSecurePoc -- --install-browsers
dotnet run --project PowerTranz3DSecurePoc
```

Close the Chromium window to end the demo. A running demo locks `bin/`, so close it before rebuilding.

## Things to know

- This is a **local demo**: credentials live in `appsettings.json` on purpose. Do not reuse this setup in production.
- The console masks secrets: `SpiToken`, network token, cryptogram and PAR are shown as `abcd…wxyz`; CVV is never
  printed.
- Staging test card `5115…0001` returns `3D1` (no 3DS) for merchant `88803255`, so it is approved without a
  challenge. A 3DS-enrolled test card from PowerTranz is needed to demo the challenge.
- An expired card makes `Api/spi/Payment` fail with `97 – Host format error`.
