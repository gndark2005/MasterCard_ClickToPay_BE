using System.Globalization;
using MC_ClickToPay.Services.Models;

namespace MC_ClickToPay.PaymentDemo.Api.Payments;

/// <summary>
/// Validates a decrypted payload and turns it into a <see cref="PaymentRequest"/>, following the field mapping of
/// ClickToPayService.Map in demo/PowerTranz3DSecurePoc. Error messages name fields and rules, never values.
/// </summary>
public sealed class PaymentRequestFactory(TimeProvider time)
{
    public const string SupportedCryptogramType = "CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM";

    public PaymentRequest Create(DecryptedPayloadDto payload, decimal amount, string currencyCode, string orderId)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var errors = new List<string>();
        var token = payload.Token?.PaymentToken?.Trim();
        if (string.IsNullOrEmpty(token) || token.Length is < 13 or > 19 || !token.All(char.IsAsciiDigit))
        {
            errors.Add("token.paymentToken must be 13 to 19 digits.");
        }

        var month = ParseMonth(payload.Token?.TokenExpirationMonth);
        if (month is null)
        {
            errors.Add("token.tokenExpirationMonth must be 01 to 12.");
        }

        var year = ParseYear(payload.Token?.TokenExpirationYear);
        if (year is null)
        {
            errors.Add("token.tokenExpirationYear must be 2 or 4 digits.");
        }

        if (month is not null && year is not null)
        {
            var now = time.GetUtcNow();
            if (year < now.Year || (year == now.Year && month < now.Month))
            {
                errors.Add("token is expired (tokenExpirationMonth/tokenExpirationYear are in the past).");
            }
        }

        var cryptogram = payload.DynamicData?.DynamicDataValue;
        if (string.IsNullOrWhiteSpace(cryptogram))
        {
            errors.Add("dynamicData.dynamicDataValue (cryptogram) is required.");
        }

        if (payload.DynamicData?.DynamicDataType != SupportedCryptogramType)
        {
            errors.Add($"dynamicData.dynamicDataType must be {SupportedCryptogramType}.");
        }

        if (errors.Count > 0)
        {
            throw new InvalidPaymentDataException(errors);
        }

        var name = FirstNonBlank(payload.Token!.CardholderFullName, payload.ConsumerFullName,
            $"{payload.ConsumerFirstName} {payload.ConsumerLastName}".Trim());

        return new PaymentRequest
        {
            OrderId = orderId,
            Amount = decimal.Round(amount, 2),
            CurrencyCode = currencyCode,
            NetworkToken = token!,
            TokenExpiration = $"{year!.Value % 100:D2}{month!.Value:D2}",
            Cryptogram = cryptogram!,
            CryptogramType = SupportedCryptogramType,
            PaymentAccountReference = payload.Token.PaymentAccountReference,
            CardholderName = name,
            BillingAddress = CreateBillingAddress(payload, name)
        };
    }

    private static PaymentBillingAddress? CreateBillingAddress(DecryptedPayloadDto payload, string? name)
    {
        var address = payload.BillingAddress ?? payload.ShippingAddress;
        var phone = payload.ConsumerMobileNumber;
        if (address is null && name is null && payload.ConsumerEmailAddress is null && phone is null)
        {
            return null;
        }

        var (firstName, lastName) = SplitName(payload.ConsumerFirstName, payload.ConsumerLastName, name);
        return new PaymentBillingAddress
        {
            FirstName = firstName,
            LastName = lastName,
            Line1 = address?.Line1,
            Line2 = address?.Line2,
            City = address?.City,
            State = address?.State,
            PostalCode = address?.Zip,
            CountryCode = address?.CountryCode,
            EmailAddress = payload.ConsumerEmailAddress,
            PhoneNumber = phone?.PhoneNumber is { } number ? $"{phone.CountryCode}{number}" : null
        };
    }

    private static int? ParseMonth(string? value) =>
        value is { Length: 1 or 2 } &&
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var month) && month is >= 1 and <= 12
            ? month
            : null;

    private static int? ParseYear(string? value) =>
        value is { Length: 2 or 4 } &&
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            ? (value.Length == 2 ? 2000 + year : year)
            : null;

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static (string? First, string? Last) SplitName(string? first, string? last, string? full)
    {
        if (!string.IsNullOrWhiteSpace(first) || !string.IsNullOrWhiteSpace(last))
        {
            return (first, last);
        }

        if (full is null)
        {
            return (null, null);
        }

        var space = full.LastIndexOf(' ');
        return space < 0 ? (full, null) : (full[..space], full[(space + 1)..]);
    }
}
