-- Click to Pay checkout state (MySQL 8). The API runs the CREATE TABLE at startup when
-- ConnectionStrings:ClickToPay is set; this file documents it and can be run by hand.
--
-- Lifecycle of a row:
--   POST /api/checkout (200)                 -> inserted as 'Pending'
--   POST /api/checkout/confirmations (JSON or batch), Mastercard 204 -> 'Confirmed'
--   ... Mastercard rejected it or failed     -> 'ConfirmationFailed' (can be confirmed again)
-- No card data is stored. spi_token is the PowerTranz SPI token: restrict access to the table.

CREATE DATABASE IF NOT EXISTS clicktopay CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS clicktopay.checkout_transactions (
    id                             BIGINT AUTO_INCREMENT PRIMARY KEY,
    src_correlation_id             VARCHAR(256)  NOT NULL,
    merchant_transaction_id        VARCHAR(255)  NULL,
    flow_id                        VARCHAR(256)  NULL,
    x_correlation_id               VARCHAR(256)  NULL,
    src_dpa_id                     VARCHAR(256)  NULL,
    spi_token                      VARCHAR(2048) NULL,
    transaction_amount             DECIMAL(14,2) NULL,
    transaction_currency           CHAR(3)       NULL,
    status                         VARCHAR(20)   NOT NULL,
    checkout_at                    DATETIME(3)   NULL     COMMENT 'UTC',
    confirmation_attempts          INT           NOT NULL DEFAULT 0,
    last_confirmation_at           DATETIME(3)   NULL     COMMENT 'UTC',
    confirmed_at                   DATETIME(3)   NULL     COMMENT 'UTC',
    checkout_event_type            CHAR(2)       NULL,
    checkout_event_status          CHAR(2)       NULL,
    confirmation_status            CHAR(2)       NULL,
    confirmation_reason            VARCHAR(255)  NULL,
    network_authorization_code     VARCHAR(64)   NULL,
    network_transaction_identifier VARCHAR(255)  NULL,
    mastercard_http_status         INT           NULL     COMMENT 'Mastercard status of the last failed confirmation',
    mastercard_detail              VARCHAR(1000) NULL     COMMENT 'Reason code and description; never payload data',
    mastercard_flow_id             VARCHAR(256)  NULL     COMMENT 'X-Src-Cx-Flow-Id returned by the last confirmation',
    created_at                     DATETIME(3)   NOT NULL COMMENT 'UTC',
    updated_at                     DATETIME(3)   NOT NULL COMMENT 'UTC',
    UNIQUE KEY ux_checkout_src_correlation_id (src_correlation_id),
    KEY ix_checkout_status (status),
    KEY ix_checkout_checkout_at (checkout_at)
) ENGINE = InnoDB;
