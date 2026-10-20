# Click to Pay daily transaction file

The Click to Pay API writes one CSV file per day; every successful checkout of the day adds a row. After the PowerTranz
payment, `POST /api/confirmations/{spiToken}` receives the payment result, confirms that row to the Mastercard
Confirmation API (`/checkout/confirmations`) and writes the outcome and the result back to it.

## Where to find it

- Folder: the `CheckoutTransactionFile:Directory` setting of the API (default: `transactions` in the API folder).
- File name: `ctp_txn_YYYYMMDD.csv` (e.g. `ctp_txn_20261012.csv`), one file per day, in the server's local time.

## What it contains

One header row, then one row per successful `POST /api/checkout` (HTTP 200). Failed checkouts are not written.

| Column | Format / example | Written by |
|---|---|---|
| date | `YYYYMMDD`, e.g. `20261012` | `/api/checkout` |
| time | `HHMMSS`, e.g. `090507` | `/api/checkout` |
| spitoken | PowerTranz SPI token | `/api/checkout` |
| transaction_amount | Decimal with a dot, e.g. `31.25` | `/api/checkout` |
| transaction_currency | ISO 4217 code, e.g. `USD` | `/api/checkout` |
| SrcDpaId | Merchant DPA | `/api/checkout` |
| SrcCorrelationId | From `checkoutWithCard()` | `/api/checkout` |
| MerchantTransactionId | From `checkoutWithCard()` | `/api/checkout` |
| FlowId | `x-src-cx-flow-id` header | `/api/checkout` |
| XCorrelationId | Caller's tracing id | `/api/checkout` |
| checkout_event_type | Two digits, e.g. `01` | `/api/confirmations/{spiToken}` |
| checkout_event_status | Two digits, e.g. `01` | `/api/confirmations/{spiToken}` |
| confirmation_status | Two digits, e.g. `01` | `/api/confirmations/{spiToken}` |
| confirmation_reason | Free text, e.g. `The payment was successful` | `/api/confirmations/{spiToken}` |
| network_authorization_code | PowerTranz `AuthorizationCode`, e.g. `123456` | `/api/confirmations/{spiToken}` |
| network_transaction_identifier | e.g. `mastercard` | `/api/confirmations/{spiToken}` |
| confirmation_result | `Confirmed` or `ConfirmationFailed` | `/api/confirmations/{spiToken}` |
| confirmation_date | `YYYYMMDD` of the last confirmation | `/api/confirmations/{spiToken}` |
| confirmation_time | `HHMMSS` of the last confirmation | `/api/confirmations/{spiToken}` |
| confirmation_detail | Mastercard's reason when it failed | `/api/confirmations/{spiToken}` |

`/api/checkout` writes the last ten columns empty, because the PowerTranz payment happens after the checkout. The
confirmation fills in the outcome from the result it receives (`approved`: 01/01/01, declined: 01/02/02;
`responseMessage` → confirmation_reason; `authorizationCode` → network_authorization_code;
`networkTransactionIdentifier` → network_transaction_identifier, `UNAVLB` when not sent) and its own result.

Values that contain a comma, a quote or a line break are wrapped in double quotes (standard CSV, RFC 4180).
See [sample-ctp_txn_20261012.csv](sample-ctp_txn_20261012.csv) for an example with fictitious data: an approved
payment already confirmed, a declined one whose confirmation failed, and two rows not confirmed yet (one written with
its outcome by hand, one as `/api/checkout` writes it).

## Confirming a transaction

```bash
curl -k -X POST https://localhost:7180/api/confirmations/<spiToken> -H "X-Api-Key: clicktopay-dev-key" \
  -H "Content-Type: application/json" -d '{"approved":true,"authorizationCode":"123456","responseMessage":"Transaction is approved"}'
```

The API looks for the row whose `spitoken` is the one in the route, in every `ctp_txn_*.csv` of the folder, newest file
first (the last matching row of a file wins). It sends `SrcCorrelationId`, `MerchantTransactionId`, `FlowId`, the
amount and the outcome received to Mastercard, then writes the outcome columns, `confirmation_result`,
`confirmation_date`, `confirmation_time` and `confirmation_detail` into that row (and the state into the database, when configured).

| Row | Result |
|---|---|
| Not confirmed yet, or `ConfirmationFailed` | Sent to Mastercard: 200 when accepted; 400/404/502 when rejected, and the row is marked `ConfirmationFailed`. |
| `confirmation_result` = `Confirmed` | Not sent again: 409. |
| Invalid amount, currency or ids | Not sent: 422, `detail` says which. |
| No row with this SPI token | 404. |

- Files written before the confirmation columns existed are accepted; the columns are added to their header when a
  row is confirmed. Unreadable files are skipped (logged as a warning).
- The row is updated by rewriting the file under the same lock the API uses to append, so do not keep the file open
  with exclusive access while the API runs. If the row cannot be updated, Mastercard has already been called: the API
  logs an error with the file, line and `SrcCorrelationId`; fill in the result by hand.

## Security

- The file holds **no card data**, but it does hold SPI tokens: restrict access to the folder. The SPI token is never
  logged or returned by the confirmation.
- Do not commit these files (`ctp_txn_*.csv` is git-ignored).

## Missing rows

The row is written after the checkout response is sent. If the API cannot write it, the checkout still succeeds and
the API logs an error with the `SrcCorrelationId` and `XCorrelationId`. Add the row by hand (same columns) before
confirming it.
