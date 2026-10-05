using System.Globalization;

namespace MC_ClickToPay.Services.Payments;

/// <summary>Request validation shared by the payment confirmation endpoints. Errors name fields and rules, never values.</summary>
public static class ConfirmPaymentRequestRules
{
    public const decimal MaximumAmount = 1_000_000m;

    public static bool IsPayloadMissing(ConfirmPaymentRequest request) =>
        string.IsNullOrWhiteSpace(request.EncryptedPayload);

    /// <summary>Checks everything except the payload presence (see <see cref="IsPayloadMissing"/>).</summary>
    public static List<string> Validate(ConfirmPaymentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<string>();
        if (request.EncryptedPayload?.Length > PayloadDecryptionService.MaximumPayloadLength)
        {
            errors.Add($"encryptedPayload must be at most {PayloadDecryptionService.MaximumPayloadLength} characters.");
        }

        if (request.TransactionAmount is not { } amount)
        {
            errors.Add("transactionAmount is required.");
        }
        else if (amount <= 0 || amount > MaximumAmount || decimal.Round(amount, 2) != amount)
        {
            errors.Add($"transactionAmount must be greater than 0, at most {MaximumAmount:0} and have at most 2 decimals.");
        }

        var currency = request.TransactionCurrencyCode;
        if (string.IsNullOrWhiteSpace(currency))
        {
            errors.Add("transactionCurrencyCode is required.");
        }
        else if (currency.Length != 3 || !(currency.All(char.IsAsciiLetterUpper) || currency.All(char.IsAsciiDigit)))
        {
            errors.Add("transactionCurrencyCode must be an ISO 4217 code: 3 uppercase letters (USD) or 3 digits (840).");
        }

        if (request.OrderId is { } orderId &&
            (orderId.Length is 0 or > 50 || !orderId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
        {
            errors.Add("orderId must be 1 to 50 letters, digits, '-' or '_'.");
        }

        if (request.Eci is { } eci && (eci.Length != 2 || !eci.All(char.IsAsciiDigit)))
        {
            errors.Add("eci must be 2 digits (assuranceData.eci from Mastercard /checkout, e.g. 02 or 06).");
        }

        return errors;
    }

    public static string NewOrderId(TimeProvider time) =>
        string.Create(CultureInfo.InvariantCulture, $"ORD-{time.GetUtcNow():yyyyMMddHHmmss}-{Guid.NewGuid():N}")[..33];
}
