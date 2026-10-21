# Migration: Click to Pay confirmation by SPI token

**For:** whoever calls the Click to Pay API after a payment (the PowerTranz HPP / front) and whoever reads the daily
transaction file. **Date:** 2026-10-21.

## Summary

| Before | Now |
|---|---|
| `POST /api/checkout/confirmations` | `POST /api/confirmations/{spiToken}` |
| JSON body with every Mastercard field (`correlationId`, `merchantTransactionId`, `flowId`, `xCorrelationId`, `confirmationData`) | The SPI token in the route and the **PowerTranz payment result** in the body |
| Batch: upload the daily CSV (`multipart/form-data`) | **Removed.** One call per transaction |
| Daily CSV: 16 columns, outcome filled in by hand | 20 columns; the confirmation fills in the outcome and its result |

The old route no longer exists (404).

## The flow

1. **HPP → `POST /api/checkout`** with the `spiToken` the PowerTranz HPP generated (unchanged). The API writes a row with
   that SPI token to the day's file `ctp_txn_YYYYMMDD.csv`.
2. **HPP → PowerTranz `/spi/Payment`.** Its response carries the same `SpiToken` and `Approved`, `AuthorizationCode`,
   `ResponseMessage`.
3. **→ `POST /api/confirmations/{spiToken}`** with that result. The API finds the row by SPI token, sends Mastercard
   `/srci/api/checkout/confirmations` and writes the outcome and result back to the row and to the database.

## Request

```
POST /api/confirmations/{spiToken}
Content-Type: application/json
X-Api-Key: <api key>
```

`{spiToken}` must be the same value sent to `/api/checkout` (URL-encode it).

Approved payment:

```json
{
  "approved": true,
  "authorizationCode": "123456",
  "responseMessage": "Transaction is approved"
}
```

Declined payment:

```json
{
  "approved": false,
  "responseMessage": "Declined by the issuer"
}
```

| Field | Required | From PowerTranz `/spi/Payment` | Sent to Mastercard as |
|---|---|---|---|
| `approved` | yes | `Approved` | `checkoutEventType` 01; `checkoutEventStatus` and `confirmationStatus` 01 (true) or 02 (false) |
| `authorizationCode` | yes when `approved` is true; up to 25 letters/digits | `AuthorizationCode` | `networkAuthorizationCode` |
| `responseMessage` | no | `ResponseMessage` | `confirmationReason`, cleaned to what Mastercard accepts (no `-`, `(`, `#`, `'`, ...) and cut to 64 |
| `networkTransactionIdentifier` | no; up to 25 of `A-Z a-z 0-9 space _ -` | see open question | `networkTransactionIdentifier`; **`UNAVLB` when not sent** |

Everything else (`srcCorrelationId`, `merchantTransactionId`, `flowId`, amount, currency) comes from the file row
written by `/api/checkout`. `confirmationTimestamp` is the current UTC time.

## Responses

| Status | When |
|---|---|
| 200 | Mastercard accepted it. Body below; headers `X-Src-Cx-Flow-Id`, `X-Correlation-Id`. |
| 400 | Body without `approved`, approved without `authorizationCode`, a value with the wrong format, or Mastercard rejected the data (reason in `detail`). |
| 401 | `X-Api-Key` missing or wrong. |
| 404 | No checkout with this SPI token (`Transaction not found.`), or Mastercard did not find the checkout. |
| 409 | Already confirmed, or another request is confirming the same SPI token. Nothing is sent again. |
| 422 | The file row has invalid data (amount, currency, ids). |
| 502 | Mastercard unreachable or failing. |
| 503 | Signing key or transaction files not available. |

```json
{
  "status": "Confirmed",
  "fileName": "ctp_txn_20261020.csv",
  "line": 5,
  "correlationId": "34f4a04b.43240031-c4d5-4821-b2ae-4c5f36298e6f",
  "xCorrelationId": "f3edc5f9-3131-4813-9880-e65ac04244a2",
  "flowId": "34f4a04b.43240031-c4d5-4821-b2ae-4c5f36298e6f.1792532096",
  "httpStatus": null,
  "detail": null
}
```

A transaction whose confirmation failed (400/404/502 from Mastercard) is marked `ConfirmationFailed` and can be
confirmed again with the same call. The SPI token is never logged or returned.

## Daily transaction file

Four columns were added at the end. Files are still read by column name, so older files keep working (the columns are
added to their header when one of their rows is confirmed).

```text
date,time,spitoken,transaction_amount,transaction_currency,SrcDpaId,SrcCorrelationId,MerchantTransactionId,FlowId,XCorrelationId,checkout_event_type,checkout_event_status,confirmation_status,confirmation_reason,network_authorization_code,network_transaction_identifier,confirmation_result,confirmation_date,confirmation_time,confirmation_detail
```

| Columns | Written by |
|---|---|
| `date` … `XCorrelationId` | `/api/checkout` (unchanged) |
| `checkout_event_type` … `network_transaction_identifier` | **the confirmation**, from the body (no longer filled in by hand) |
| `confirmation_result` (`Confirmed` / `ConfirmationFailed`), `confirmation_date`, `confirmation_time`, `confirmation_detail` | **the confirmation** (new) |

Example row after an approved confirmation:

```text
20261020,171956,<spiToken>,6.00,EUR,823ef281-...,34f4a04b.4324...,0a4e0d3.34f4a04b...,34f4a04b.4324....1792532096,f3edc5f9-...,01,01,01,Transaction is approved,123456,UNAVLB,Confirmed,20261020,171958,
```

The database (`checkout_transactions`) is unchanged: `Pending` after the checkout, `Confirmed` or
`ConfirmationFailed` after the confirmation.

## What to change on your side

- **Front / HPP:** after `/spi/Payment`, call `POST /api/confirmations/{SpiToken}` with `Approved`,
  `AuthorizationCode` and `ResponseMessage` of its response. Stop calling `/api/checkout/confirmations`.
- **Batch process on the CSV:** no longer needed. Do not fill in the outcome columns by hand; read
  `confirmation_result` to see what was confirmed.

## Verified

2026-10-20, Mastercard sandbox + PowerTranz staging, real Click to Pay checkout (FPAN, ECI 06), payment approved
(`00`, authorization `123456`): Mastercard accepted the confirmation and the row was written as above. Without
`networkTransactionIdentifier` Mastercard answers `400 [INVALID_ARGUMENT]`; that is why the API sends `UNAVLB`.

## Open questions

1. **`networkTransactionIdentifier`.** Mastercard (`checkout1.0.yaml`, `ConfirmationData`): *"The unique authorisation
   related tracing value assigned by a Payment Network and provided in an authorisation response. Required only when
   checkoutEventType=01. If checkoutEventType=01 and the value of networkTransactionIdentifier is unknown, please pass
   UNAVLB."* Which PowerTranz `/spi/Payment` field carries it, if any, is still to be confirmed with PowerTranz
   (`TransactionIdentifier` is PowerTranz's own id, `RRN` the acquirer's). Until then the API sends `UNAVLB`.
2. **`UNAVLB` against the sandbox** has not been run with a real checkout yet (only `mastercard` was).
3. **Declined payment** has not been run end to end with real data yet.

## Testing

`src/MC_ClickToPay.Api/MC_ClickToPay.Api.http` has the step-by-step requests (checkout, confirmation, 409, 404, 400,
401). Locally, `MastercardApi:Simulate = true` (Development only) skips Mastercard, so any ids and SPI token work.
Against Mastercard, the ids must come from a real `checkoutWithCard()` a few minutes old; the SPI token can be any value.
