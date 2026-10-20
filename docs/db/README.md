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

The API enables it when `ConnectionStrings:ClickToPay` is set. Without the connection string nothing is recorded.
The database and the user must exist ([schema.sql](schema.sql)); the tables come from the migrations below.

## Migrations (applied when the API starts)

The schema is versioned in SQL scripts embedded in the `MC_ClickToPay.Services` DLL, so every deployment carries them:
[`src/MC_ClickToPay.Services/MC_ClickToPay.Services/Database/Migrations/`](../../src/MC_ClickToPay.Services/MC_ClickToPay.Services/Database/Migrations/).

| Migration | What |
|---|---|
| `0001_create_checkout_transactions.sql` | Table `checkout_transactions` |

When the API starts (`DatabaseMigrator`, called by `CheckoutSchemaInitializer`):

1. It creates `schema_migrations` if needed: one row per applied migration (`version`, `name`, SHA-256 `checksum`,
   `applied_at`, `execution_ms`).
2. It applies, in version order, every migration not yet in `schema_migrations`, and records it. Each runs once per
   database. A MySQL named lock (`GET_LOCK`) lets only one instance migrate at a time.
3. Then it serves requests.

| Situation at startup | What happens |
|---|---|
| Database up to date | Log `Database schema is up to date`; nothing runs |
| Pending migrations | They are applied; log `Database schema migrated: applied 0002_...` |
| A migration fails (SQL error) | **The API does not start**; the log names the migration and the MySQL error |
| An applied migration was edited (checksum changed) | **The API does not start**: never edit an applied migration |
| MySQL unreachable | The API starts and logs an error; nothing is recorded until MySQL is reachable. Restart the API then, so the migrations run |

A database created before migrations existed (table made at startup) is adopted: `0001` uses `IF NOT EXISTS`, so it
only records itself and the data is untouched.

### Adding a migration

1. New file in `Database/Migrations/` with the next version: `0002_short_description.sql` (four digits, lowercase,
   underscores). It is embedded automatically (`EmbeddedResource` in the csproj).
2. Make it re-runnable where possible (`ADD COLUMN IF NOT EXISTS` is not in MySQL 8.4: check `information_schema` or keep
   one statement per migration). MySQL commits DDL one statement at a time, so a migration that fails halfway is not
   rolled back.
3. **Never edit or rename a migration that has been deployed**: add a new one. Once deployed, add its checksum to
   `DeployedMigrationsAreNotEdited` in `tests/MC_ClickToPay.Services.Tests/DatabaseMigratorTests.cs`.
4. Update the code that uses the new columns in the same change.

The app user needs `CREATE, ALTER, INDEX` (plus `SELECT, INSERT, UPDATE`) on the database to apply migrations.

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

The API connects as `clicktopay_app` (only `SELECT, INSERT, UPDATE, CREATE, ALTER, INDEX` on `clicktopay.*`); its connection string
is in `src/MC_ClickToPay.Api/appsettings.Local.json` (git-ignored):

```json
"ConnectionStrings": { "ClickToPay": "Server=127.0.0.1;Port=3306;Database=clicktopay;User ID=clicktopay_app;Password=...;" }
```

### Setting it up on another machine

1. Download the Windows ZIP archive from https://dev.mysql.com/downloads/mysql/ and check its MD5.
2. Extract it to `C:\mysql-portable\server` and copy the `my.ini` above (paths with forward slashes).
3. `server\bin\mysqld.exe --defaults-file=C:\mysql-portable\my.ini --initialize-insecure`, then `start-mysql.ps1`.
4. As root (empty password at first): `ALTER USER 'root'@'localhost' IDENTIFIED BY '<random>';`, then run
   [schema.sql](schema.sql) with the app user's password (database, user and grants).
5. Put the connection string in `appsettings.Local.json` (or `appsettings.Production.json` on a server) and start the
   API: the migrations create the tables.

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
