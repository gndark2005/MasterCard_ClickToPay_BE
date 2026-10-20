using System.Text.Json.Serialization;
using MC_ClickToPay.Services.Models;

namespace MC_ClickToPay.Services.Checkout;

/// <summary>
/// What the UI sends after checkoutWithCard(): the Mastercard values (checkoutResponseData and headers) plus the
/// SPI token the PowerTranz HPP generated for its payment. The SPI token is not used by the checkout (the HPP calls
/// PowerTranz itself with the decrypted card); it is never logged and is written to the daily transaction file, where
/// POST /api/confirmations/{spiToken} finds the transaction to confirm.
/// </summary>
public sealed class CompleteCheckoutRequest
{
    [JsonPropertyName("spiToken")]
    public string SpiToken { get; init; } = string.Empty;

    /// <summary>The merchant's DPA: it varies per merchant and is sent as is to Mastercard /checkout.</summary>
    [JsonPropertyName("srcDpaId")]
    public string SrcDpaId { get; init; } = string.Empty;

    /// <summary>checkoutResponseData.srcCorrelationId returned by checkoutWithCard().</summary>
    [JsonPropertyName("srcCorrelationId")]
    public string SrcCorrelationId { get; init; } = string.Empty;

    [JsonPropertyName("merchantTransactionId")]
    public string MerchantTransactionId { get; init; } = string.Empty;

    /// <summary>x-src-cx-flow-id header returned by checkoutWithCard().</summary>
    [JsonPropertyName("flowId")]
    public string FlowId { get; init; } = string.Empty;

    /// <summary>Order amount, the same one given to checkoutWithCard(); sent to Mastercard /checkout.</summary>
    [JsonPropertyName("transactionAmount")]
    public decimal TransactionAmount { get; init; }

    /// <summary>ISO 4217 alphabetic code, e.g. "USD".</summary>
    [JsonPropertyName("transactionCurrencyCode")]
    public string TransactionCurrencyCode { get; init; } = string.Empty;

    /// <summary>The UI's own tracing id: logged and echoed in the X-Correlation-Id response header, not sent to Mastercard.</summary>
    [JsonPropertyName("xCorrelationId")]
    public string XCorrelationId { get; init; } = string.Empty;
}

public sealed class CompleteCheckoutResult
{
    [JsonPropertyName("merchantTransactionId")]
    public string? MerchantTransactionId { get; init; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    /// <summary>assuranceData.eci from the /checkout response (it is not inside the encrypted payload).</summary>
    [JsonPropertyName("eci")]
    public string? Eci { get; init; }

    /// <summary>The decrypted payload; its concrete model follows dynamicData.dynamicDataType.</summary>
    [JsonPropertyName("payload")]
    public required DecryptedPayloadDto Payload { get; init; }
}

/// <summary>
/// One confirmation for Mastercard /checkout/confirmations, built from a transaction file row: its body
/// (correlationId, merchantTransactionId, confirmationData) plus the two values Mastercard takes as headers (flowId →
/// X-Src-Cx-Flow-Id, merchantTransactionId → merchant-transaction-id) and the transaction's own tracing id.
/// </summary>
public sealed class CheckoutConfirmationRequest
{
    /// <summary>The srcCorrelationId of the checkout (checkoutWithCard() / POST /api/checkout).</summary>
    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; init; } = string.Empty;

    /// <summary>Sent in the body and in the merchant-transaction-id header. Optional for subsequent payments.</summary>
    [JsonPropertyName("merchantTransactionId")]
    public string? MerchantTransactionId { get; init; }

    /// <summary>X-Src-Cx-Flow-Id of the checkout, so Mastercard routes the call to the same server.</summary>
    [JsonPropertyName("flowId")]
    public string? FlowId { get; init; }

    /// <summary>The caller's own tracing id: logged and echoed in the X-Correlation-Id response header, not sent to Mastercard.</summary>
    [JsonPropertyName("xCorrelationId")]
    public string XCorrelationId { get; init; } = string.Empty;

    [JsonPropertyName("confirmationData")]
    public ConfirmationData? ConfirmationData { get; init; }
}

/// <summary>Mastercard ConfirmationData: the outcome of the checkout or payment, sent as is.</summary>
public sealed class ConfirmationData
{
    /// <summary>Two-digit code, e.g. "01".</summary>
    [JsonPropertyName("checkoutEventType")]
    public string CheckoutEventType { get; init; } = string.Empty;

    /// <summary>Two-digit code, e.g. "01".</summary>
    [JsonPropertyName("checkoutEventStatus")]
    public string CheckoutEventStatus { get; init; } = string.Empty;

    /// <summary>Two-digit code, e.g. "01".</summary>
    [JsonPropertyName("confirmationStatus")]
    public string ConfirmationStatus { get; init; } = string.Empty;

    [JsonPropertyName("confirmationReason")]
    public string? ConfirmationReason { get; init; }

    /// <summary>ISO 8601. Optional here: the current UTC time is sent when omitted.</summary>
    [JsonPropertyName("confirmationTimestamp")]
    public DateTimeOffset? ConfirmationTimestamp { get; init; }

    /// <summary>Authorization code returned by the processor (e.g. PowerTranz AuthorizationCode).</summary>
    [JsonPropertyName("networkAuthorizationCode")]
    public string? NetworkAuthorizationCode { get; init; }

    [JsonPropertyName("networkTransactionIdentifier")]
    public string? NetworkTransactionIdentifier { get; init; }

    [JsonPropertyName("transactionAmount")]
    public ConfirmationAmount? TransactionAmount { get; init; }
}

public sealed class ConfirmationAmount
{
    /// <summary>A JSON number or a string as in Mastercard's sample ("1200"); sent with two decimals.</summary>
    [JsonPropertyName("transactionAmount")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public decimal Amount { get; init; }

    /// <summary>ISO 4217 alphabetic code, e.g. "USD".</summary>
    [JsonPropertyName("transactionCurrencyCode")]
    public string CurrencyCode { get; init; } = string.Empty;
}

public sealed class CheckoutConfirmationResult
{
    /// <summary>X-Src-Cx-Flow-Id returned by Mastercard: the one to use in later calls for this checkout.</summary>
    public string? FlowId { get; init; }
}
