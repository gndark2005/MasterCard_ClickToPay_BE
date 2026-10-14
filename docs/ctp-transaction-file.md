# Click to Pay daily transaction file

The Click to Pay API writes one CSV file per day with every successful checkout. The batch process uses it to call
the Mastercard Confirmation API (`/checkout/confirmations`) outside the checkout.

## Where to find it

- Folder: the `CheckoutTransactionFile:Directory` setting of the API (default: `transactions` in the API folder).
- File name: `ctp_txn_YYYYMMDD.csv` (e.g. `ctp_txn_20261012.csv`), one file per day, in the server's local time.

## What it contains

One header row, then one row per successful `POST /api/checkout` (HTTP 200). Failed checkouts are not written.

| Column | Format / example |
|---|---|
| date | `YYYYMMDD`, e.g. `20261012` |
| time | `HHMMSS`, e.g. `090507` |
| spitoken | PowerTranz SPI token |
| transaction_amount | Decimal with a dot, e.g. `31.25` |
| transaction_currency | ISO 4217 code, e.g. `USD` |
| SrcDpaId | Merchant DPA |
| SrcCorrelationId | From `checkoutWithCard()` |
| MerchantTransactionId | From `checkoutWithCard()` |
| FlowId | `x-src-cx-flow-id` header |
| XCorrelationId | Caller's tracing id |

Values that contain a comma or a quote are wrapped in double quotes (standard CSV).
See [sample-ctp_txn_20261012.csv](sample-ctp_txn_20261012.csv) for an example with fictitious data.

## Instructions for the batch process

1. Process the **previous day's** file. If you must read today's file, open it with shared read/write access,
   because the API may still be adding rows.
2. Skip the header row.
3. For each row, call the Mastercard Confirmation API using `SrcCorrelationId`, `MerchantTransactionId`, `FlowId`,
   `transaction_amount` and `transaction_currency`.
4. Do not modify or delete the file while the API may still write to it (that is, during its own day).

## Security

- The file holds **no card data**, but it does hold SPI tokens: restrict access to the folder.
- Do not commit these files (`ctp_txn_*.csv` is git-ignored).

## Missing rows

The row is written after the checkout response is sent. If the API cannot write it, the checkout still succeeds and
the API logs an error with the `SrcCorrelationId` and `XCorrelationId`. Confirm that transaction manually.
