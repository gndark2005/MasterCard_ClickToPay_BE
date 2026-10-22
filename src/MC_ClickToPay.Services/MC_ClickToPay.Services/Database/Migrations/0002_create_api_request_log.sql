-- 0002: one row per request to /api/* (successful or not), for audit and support.
-- Never request or response bodies, card data or SPI tokens: route is the template (/api/confirmations/{spiToken}).
CREATE TABLE IF NOT EXISTS api_request_log (
    id                 BIGINT AUTO_INCREMENT PRIMARY KEY,
    request_at         DATETIME(3)   NOT NULL COMMENT 'UTC',
    method             VARCHAR(10)   NOT NULL,
    route              VARCHAR(200)  NOT NULL COMMENT 'Route template, never the SPI token',
    http_status        INT           NOT NULL,
    duration_ms        INT           NOT NULL,
    src_correlation_id VARCHAR(256)  NULL,
    x_correlation_id   VARCHAR(256)  NULL,
    error_title        VARCHAR(255)  NULL,
    error_detail       VARCHAR(2000) NULL     COMMENT 'Problem detail or validation errors; never payload or card data',
    exception_type     VARCHAR(255)  NULL     COMMENT 'Unhandled exception, when the request failed with one',
    client_ip          VARCHAR(64)   NULL,
    trace_id           VARCHAR(64)   NULL,
    KEY ix_api_request_log_request_at (request_at),
    KEY ix_api_request_log_src_correlation_id (src_correlation_id),
    KEY ix_api_request_log_http_status (http_status)
) ENGINE = InnoDB;
