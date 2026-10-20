-- Click to Pay checkout state (MySQL 8): one-time setup of the database and the app user, run as root.
-- The TABLES are not created here: they come from the migrations embedded in the API
-- (src/MC_ClickToPay.Services/MC_ClickToPay.Services/Database/Migrations), applied when the API starts.
--
-- Lifecycle of a row in checkout_transactions:
--   POST /api/checkout (200)                                        -> inserted as 'Pending'
--   POST /api/checkout/confirmations (JSON or batch), Mastercard 204 -> 'Confirmed'
--   ... Mastercard rejected it or failed                            -> 'ConfirmationFailed' (can be confirmed again)
-- No card data is stored. spi_token is the PowerTranz SPI token: restrict access to the tables.
--
-- Replace <app-password> with a random password and put the same one in ConnectionStrings:ClickToPay.

CREATE DATABASE IF NOT EXISTS clicktopay CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

CREATE USER IF NOT EXISTS 'clicktopay_app'@'localhost' IDENTIFIED BY '<app-password>';
CREATE USER IF NOT EXISTS 'clicktopay_app'@'127.0.0.1' IDENTIFIED BY '<app-password>';

-- SELECT/INSERT/UPDATE for the data; CREATE/ALTER/INDEX so the API can apply its migrations. No DELETE, no other databases.
GRANT SELECT, INSERT, UPDATE, CREATE, ALTER, INDEX ON clicktopay.* TO 'clicktopay_app'@'localhost';
GRANT SELECT, INSERT, UPDATE, CREATE, ALTER, INDEX ON clicktopay.* TO 'clicktopay_app'@'127.0.0.1';
