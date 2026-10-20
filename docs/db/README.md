# Checkout state database (MySQL)

The API records every checkout in the MySQL table `checkout_transactions` ([schema.sql](schema.sql)):

| Event | `status` |
|---|---|
| `POST /api/checkout` answers 200 | `Pending` |
| `POST /api/checkout/confirmations` (JSON or batch file), Mastercard answers 204 | `Confirmed` |
| Mastercard rejects the confirmation or cannot be reached | `ConfirmationFailed` (confirm it again later) |

- `confirmation_attempts` counts every confirmation; a `Confirmed` row stays `Confirmed` if a later attempt fails
  (`mastercard_http_status` / `mastercard_detail` then show that last failure).
- A confirmation for a checkout that has no row (recorded before the database existed, or from another server) creates
  its row with the confirmation's data.
- A repeated checkout (same `srcCorrelationId`) refreshes its data and keeps its status.
- The database never fails a request: Mastercard has already been called, so an error is only logged with the ids.
- No card data is stored. `spi_token` is the PowerTranz SPI token: keep access to the table restricted.
- The daily CSV file is still written as a backup and for batch uploads.

The API enables it when `ConnectionStrings:ClickToPay` is set and creates the table at startup when it is missing
(the database and the user must exist). Without the connection string nothing is recorded.

## Local development: portable MySQL (no administrator rights)

Installed on this machine in `C:\mysql-portable` (MySQL Community Server 8.4.11 LTS, ZIP archive, MD5 verified):

| Path | What |
|---|---|
| `C:\mysql-portable\server` | MySQL binaries |
| `C:\mysql-portable\data` | Data folder |
| `C:\mysql-portable\my.ini` | Configuration: `127.0.0.1:3306` only, utf8mb4, UTC, X protocol off |
| `C:\mysql-portable\mysqld.err` | Server log |
| `C:\mysql-portable\root-password.txt` | `root` password (the folder is readable by your user only) |

An ASCII path is used on purpose: MySQL on Windows does not handle non-ASCII data folders well.

It does not run as a Windows service, so it does not start with Windows:

```powershell
.\docs\db\start-mysql.ps1   # after a reboot
.\docs\db\stop-mysql.ps1
```

The API connects as `clicktopay_app` (only `SELECT, INSERT, UPDATE, CREATE` on `clicktopay.*`); its connection string
is in `src/MC_ClickToPay.Api/appsettings.Local.json` (git-ignored):

```json
"ConnectionStrings": { "ClickToPay": "Server=127.0.0.1;Port=3306;Database=clicktopay;User ID=clicktopay_app;Password=...;" }
```

### Setting it up on another machine

1. Download the Windows ZIP archive from https://dev.mysql.com/downloads/mysql/ and check its MD5.
2. Extract it to `C:\mysql-portable\server` and copy the `my.ini` above (paths with forward slashes).
3. `server\bin\mysqld.exe --defaults-file=C:\mysql-portable\my.ini --initialize-insecure`, then `start-mysql.ps1`.
4. As root (empty password at first):

   ```sql
   ALTER USER 'root'@'localhost' IDENTIFIED BY '<random>';
   CREATE DATABASE clicktopay CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
   CREATE USER 'clicktopay_app'@'localhost' IDENTIFIED BY '<random>';
   CREATE USER 'clicktopay_app'@'127.0.0.1' IDENTIFIED BY '<random>';
   GRANT SELECT, INSERT, UPDATE, CREATE ON clicktopay.* TO 'clicktopay_app'@'localhost';
   GRANT SELECT, INSERT, UPDATE, CREATE ON clicktopay.* TO 'clicktopay_app'@'127.0.0.1';
   ```

5. Put the connection string in `appsettings.Local.json` and start the API: it creates the table.

### Useful queries

```sql
-- Today's checkouts and their state
SELECT src_correlation_id, status, transaction_amount, transaction_currency, confirmation_attempts,
       mastercard_http_status, checkout_at, confirmed_at
FROM clicktopay.checkout_transactions
WHERE checkout_at >= UTC_DATE()
ORDER BY checkout_at;

-- Still to confirm, or to retry
SELECT src_correlation_id, merchant_transaction_id, flow_id, status, mastercard_detail
FROM clicktopay.checkout_transactions
WHERE status IN ('Pending', 'ConfirmationFailed');
```
