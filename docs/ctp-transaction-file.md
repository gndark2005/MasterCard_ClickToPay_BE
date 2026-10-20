# Click to Pay daily transaction file

The Click to Pay API writes one CSV file per day with every successful checkout. Once the payment outcome of each row
is filled in, the file is uploaded to `POST /api/checkout/confirmations`, which confirms every row to the Mastercard
Confirmation API (`/checkout/confirmations`).

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
| checkout_event_type | Two digits, e.g. `01` | **empty; fill in after the payment** |
| checkout_event_status | Two digits, e.g. `01` | **empty; fill in after the payment** |
| confirmation_status | Two digits, e.g. `01` | **empty; fill in after the payment** |
| confirmation_reason | Free text, e.g. `The payment was successful` | **empty; optional** |
| network_authorization_code | PowerTranz `AuthorizationCode`, e.g. `123456` | **empty; optional** |
| network_transaction_identifier | e.g. `mastercard` | **empty; optional** |

The last six columns are the payment outcome. `/api/checkout` writes them empty, because the PowerTranz payment
happens after the checkout. Whoever has the payment result must fill them in before the file is uploaded.

Values that contain a comma, a quote or a line break are wrapped in double quotes (standard CSV, RFC 4180).
See [sample-ctp_txn_20261012.csv](sample-ctp_txn_20261012.csv) for an example with fictitious data: an approved
payment, a declined one, another approved one and a row without an outcome yet.

## Confirming the file (batch)

Upload it as `multipart/form-data`, in the `file` field:

```bash
curl -k -X POST https://localhost:7180/api/checkout/confirmations \
  -H "X-Api-Key: clicktopay-dev-key" \
  -F "file=@ctp_txn_20261012.csv;type=text/csv"
```

Each row is confirmed with `SrcCorrelationId`, `MerchantTransactionId`, `FlowId`, `XCorrelationId`, the amount and the
outcome columns, exactly like a single JSON confirmation. The response (HTTP 200) has one result per row, in file order:

| status | Meaning |
|---|---|
| `Confirmed` | Mastercard accepted it; `flowId` is the X-Src-Cx-Flow-Id it returned. |
| `Failed` | Mastercard rejected it or could not be reached; `httpStatus` (400, 404 or 502) and `detail` say why. |
| `Invalid` | The row's data is invalid (codes, amount, currency, lengths); it was not sent. |
| `Skipped` | The outcome columns are empty; it was not sent. |

- Columns are found by name, in any order. Files written before the outcome columns existed are accepted; their rows
  are `Skipped`.
- A file that is empty, malformed or missing a required column returns 400 and nothing is sent.
- Up to 10 MiB and 10,000 rows per file. Rows are sent four at a time.
- Uploading the same file again sends the confirmations again: upload each file once, or only the rows that failed.

Instructions for the batch process:

1. Process the **previous day's** file. If you must read today's file, open it with shared read/write access,
   because the API may still be adding rows.
2. Fill in the outcome columns from the PowerTranz results.
3. Upload it and keep the response: retry only the `Failed` rows (in a new file with the same header).
4. Do not modify or delete the file while the API may still write to it (that is, during its own day).

## Security

- The file holds **no card data**, but it does hold SPI tokens: restrict access to the folder. The `spitoken` column is
  never read by the confirmation and never returned.
- Do not commit these files (`ctp_txn_*.csv` is git-ignored).

## Missing rows

The row is written after the checkout response is sent. If the API cannot write it, the checkout still succeeds and
the API logs an error with the `SrcCorrelationId` and `XCorrelationId`. Confirm that transaction manually (JSON
confirmation).
