using System.Globalization;
using MC_ClickToPay.Services.Models;

namespace MC_ClickToPay.Services.Payments;

/// <summary>
/// Validates a decrypted payload and turns it into a <see cref="PaymentRequest"/>, following the field mapping of
/// ClickToPayService.Map in demo/PowerTranz3DSecurePoc. The payload model, chosen from dynamicData.dynamicDataType,
/// decides the credential:
/// <list type="bullet">
/// <item><see cref="TokenizedPayloadDto"/>: network token + DSRP cryptogram;</item>
/// <item><see cref="DsrpPanPayloadDto"/>: PAN + DSRP cryptogram (an unmasked token is used instead: dual payload);</item>
/// <item><see cref="DynamicSecurityCodePayloadDto"/>: PAN + DTVC (sent as the CVV);</item>
/// <item><see cref="FpanPayloadDto"/>: PAN only.</item>
/// </list>
/// Error messages name fields and rules, never values.
/// </summary>
public sealed class PaymentRequestFactory(TimeProvider time)
{
    public const string SupportedCryptogramType = DynamicDataTypes.Cryptogram;

    public const string NoCryptogramType = DynamicDataTypes.None;

    public const string DynamicSecurityCodeType = DynamicDataTypes.DynamicSecurityCode;

    public PaymentRequest Create(DecryptedPayloadDto payload, decimal amount, string currencyCode, string orderId,
        string? eci = null)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var errors = new List<string>();
        var dynamicValue = payload.DynamicData?.DynamicDataValue;
        Credential credential;
        string? cryptogram = null;
        string? securityCode = null;
        string dynamicDataType;

        switch (payload)
        {
            case TokenizedPayloadDto tokenized:
                credential = FromToken(errors, tokenized.Token);
                cryptogram = RequireCryptogram(errors, dynamicValue);
                dynamicDataType = DynamicDataTypes.Cryptogram;
                break;

            case DsrpPanPayloadDto dsrpPan:
                // Dual payload: an unmasked token is the authorization credential; otherwise the PAN.
                credential = IsRealNumber(dsrpPan.Token?.PaymentToken)
                    ? FromToken(errors, dsrpPan.Token)
                    : FromCard(errors, dsrpPan.Card);
                cryptogram = RequireCryptogram(errors, dynamicValue);
                dynamicDataType = DynamicDataTypes.Cryptogram;
                break;

            case DynamicSecurityCodePayloadDto dtvc:
                credential = FromCard(errors, dtvc.Card);
                securityCode = dynamicValue?.Trim();
                if (securityCode is not { Length: 3 or 4 } || !securityCode.All(char.IsAsciiDigit))
                {
                    errors.Add("dynamicData.dynamicDataValue (dynamic security code) must be 3 or 4 digits.");
                }

                dynamicDataType = DynamicDataTypes.DynamicSecurityCode;
                break;

            case FpanPayloadDto fpan:
                credential = FromCard(errors, fpan.Card);
                dynamicDataType = DynamicDataTypes.None;
                break;

            default:
                throw new InvalidPaymentDataException(["dynamicData.dynamicDataType is not supported."]);
        }

        if (errors.Count > 0)
        {
            throw new InvalidPaymentDataException(errors);
        }

        var name = FirstNonBlank(payload.GetCredentialCardholderName(), payload.ConsumerFullName,
            $"{payload.ConsumerFirstName} {payload.ConsumerLastName}".Trim());

        return new PaymentRequest
        {
            OrderId = orderId,
            Amount = decimal.Round(amount, 2),
            CurrencyCode = currencyCode,
            CredentialType = credential.Type,
            AccountNumber = credential.Number!,
            Expiration = $"{credential.Year!.Value % 100:D2}{credential.Month!.Value:D2}",
            Cryptogram = cryptogram,
            SecurityCode = securityCode,
            CryptogramType = dynamicDataType,
            Eci = string.IsNullOrWhiteSpace(eci) ? null : eci,
            PaymentAccountReference = payload.GetCredentialPaymentAccountReference(),
            CardholderName = name,
            BillingAddress = CreateBillingAddress(payload, name)
        };
    }

    private Credential FromToken(List<string> errors, PaymentTokenDto? token)
    {
        if (string.IsNullOrWhiteSpace(token?.PaymentToken))
        {
            errors.Add("token.paymentToken is required.");
            return new Credential(PaymentCredentialType.NetworkToken, null, null, null);
        }

        var number = ValidateAccount(errors, "token.paymentToken", token.PaymentToken);
        var (month, year) = ValidateExpiry(errors, "token.tokenExpirationMonth", token.TokenExpirationMonth,
            "token.tokenExpirationYear", token.TokenExpirationYear);
        return new Credential(PaymentCredentialType.NetworkToken, number, month, year);
    }

    private Credential FromCard(List<string> errors, PaymentCardDto? card)
    {
        if (string.IsNullOrWhiteSpace(card?.PrimaryAccountNumber))
        {
            errors.Add("card.primaryAccountNumber is required.");
            return new Credential(PaymentCredentialType.Pan, null, null, null);
        }

        var number = ValidateAccount(errors, "card.primaryAccountNumber", card.PrimaryAccountNumber);
        var (month, year) = ValidateExpiry(errors, "card.panExpirationMonth", card.PanExpirationMonth,
            "card.panExpirationYear", card.PanExpirationYear);
        return new Credential(PaymentCredentialType.Pan, number, month, year);
    }

    private static string? RequireCryptogram(List<string> errors, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add("dynamicData.dynamicDataValue (cryptogram) is required.");
        }

        return value;
    }

    private static bool IsRealNumber(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().All(char.IsAsciiDigit);

    private static string? ValidateAccount(List<string> errors, string field, string value)
    {
        var number = value.Trim();
        if (number.Length is < 12 or > 19 || !number.All(char.IsAsciiDigit))
        {
            errors.Add($"{field} must be 12 to 19 digits.");
            return null;
        }

        return number;
    }

    private (int? Month, int? Year) ValidateExpiry(List<string> errors, string monthField, string? monthValue,
        string yearField, string? yearValue)
    {
        var month = ParseMonth(monthValue);
        if (month is null)
        {
            errors.Add($"{monthField} must be 01 to 12.");
        }

        var year = ParseYear(yearValue);
        if (year is null)
        {
            errors.Add($"{yearField} must be 2 or 4 digits.");
        }

        if (month is not null && year is not null)
        {
            var now = time.GetUtcNow();
            if (year < now.Year || (year == now.Year && month < now.Month))
            {
                errors.Add($"{monthField[..monthField.IndexOf('.')]} is expired ({monthField}/{yearField} are in the past).");
            }
        }

        return (month, year);
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

    // A class, not a record, so ToString() never prints the account number.
    private sealed class Credential(PaymentCredentialType type, string? number, int? month, int? year)
    {
        public PaymentCredentialType Type { get; } = type;

        public string? Number { get; } = number;

        public int? Month { get; } = month;

        public int? Year { get; } = year;
    }
}
