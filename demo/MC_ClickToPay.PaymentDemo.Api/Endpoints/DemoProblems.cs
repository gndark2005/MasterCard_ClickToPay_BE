namespace MC_ClickToPay.PaymentDemo.Api.Endpoints;

/// <summary>
/// Problem Details (RFC 9457) with a stable machine-readable "code" and, for validation errors, an "errors" list
/// of field rules. Nothing here ever carries payload, card or key data.
/// </summary>
public static class DemoProblems
{
    public const string MissingPayload = "missing_payload";
    public const string InvalidRequest = "invalid_request";
    public const string InvalidPayload = "invalid_payload";
    public const string DecryptionFailed = "decryption_failed";
    public const string InvalidPaymentData = "invalid_payment_data";
    public const string PaymentProcessingFailed = "payment_processing_failed";
    public const string DecryptionUnavailable = "decryption_unavailable";
    public const string SamplePayloadsDisabled = "sample_payloads_disabled";
    public const string UnexpectedError = "unexpected_error";

    public static IResult Create(int statusCode, string code, string title, string? detail = null,
        IReadOnlyList<string>? errors = null)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = code };
        if (errors is { Count: > 0 })
        {
            extensions["errors"] = errors;
        }

        return Results.Problem(detail: detail, statusCode: statusCode, title: title, extensions: extensions);
    }
}
