using System.Text.Json.Serialization;
using MC_ClickToPay.Services.Checkout;
using MC_ClickToPay.Services.Exceptions;
using MC_ClickToPay.Services.Models;

namespace MC_ClickToPay.Api.Endpoints.Checkout;

/// <summary>
/// Response of POST /api/checkout: the decrypted card and addresses as flat fields, ready for the UI's PowerTranz SPI
/// request. CardNo and CardExpDate are always set; every other field is null when Mastercard did not send it (the UI
/// drops null fields before calling PowerTranz).
/// </summary>
public sealed class CompleteCheckoutResponse
{
    /// <summary>The PAN, or the network token number when Mastercard sent only a token.</summary>
    [JsonPropertyName("cardNo")]
    public required string CardNo { get; init; }

    /// <summary>YYMM, the PowerTranz CardExpiration format.</summary>
    [JsonPropertyName("cardExpDate")]
    public required string CardExpDate { get; init; }

    /// <summary>Dynamic security code (DYNAMIC_CARD_SECURITY_CODE payloads only).</summary>
    [JsonPropertyName("cardCVV2")]
    public string? CardCvv2 { get; init; }

    [JsonPropertyName("cardholderName")]
    public string? CardholderName { get; init; }

    [JsonPropertyName("billToFirstName")]
    public string? BillToFirstName { get; init; }

    [JsonPropertyName("billToLastName")]
    public string? BillToLastName { get; init; }

    [JsonPropertyName("billToAddress1")]
    public string? BillToAddress1 { get; init; }

    [JsonPropertyName("billToAddress2")]
    public string? BillToAddress2 { get; init; }

    [JsonPropertyName("billToState")]
    public string? BillToState { get; init; }

    [JsonPropertyName("billToPostCode")]
    public string? BillToPostCode { get; init; }

    /// <summary>ISO 3166 alpha-2, as Mastercard sends it (e.g. "US").</summary>
    [JsonPropertyName("billToCountry")]
    public string? BillToCountry { get; init; }

    /// <summary>Country calling code followed by the number, digits only (e.g. "12125550100").</summary>
    [JsonPropertyName("billToTelephone")]
    public string? BillToTelephone { get; init; }

    [JsonPropertyName("billToEmail")]
    public string? BillToEmail { get; init; }

    [JsonPropertyName("shipToFirstName")]
    public string? ShipToFirstName { get; init; }

    [JsonPropertyName("shipToLastName")]
    public string? ShipToLastName { get; init; }

    [JsonPropertyName("shipToAddress1")]
    public string? ShipToAddress1 { get; init; }

    [JsonPropertyName("shipToAddress2")]
    public string? ShipToAddress2 { get; init; }

    [JsonPropertyName("shipToState")]
    public string? ShipToState { get; init; }

    [JsonPropertyName("shipToPostCode")]
    public string? ShipToPostCode { get; init; }

    [JsonPropertyName("shipToCountry")]
    public string? ShipToCountry { get; init; }

    [JsonPropertyName("shipToTelephone")]
    public string? ShipToTelephone { get; init; }

    [JsonPropertyName("shipToEmail")]
    public string? ShipToEmail { get; init; }

    /// <summary>Maps the decrypted payload; throws when it has no card number or expiry.</summary>
    public static CompleteCheckoutResponse From(CheckoutPanPayloadDto payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var card = payload.Card;
        if (string.IsNullOrWhiteSpace(card?.PrimaryAccountNumber))
        {
            throw new MastercardCheckoutException("The decrypted payload has no card number.");
        }

        var month = card.PanExpirationMonth?.Trim() ?? string.Empty;
        var year = card.PanExpirationYear?.Trim() ?? string.Empty;
        if (month.Length is < 1 or > 2 || year.Length < 2 || !month.All(char.IsAsciiDigit) || !year.All(char.IsAsciiDigit))
        {
            throw new MastercardCheckoutException("The decrypted payload has no valid card expiry.");
        }

        var consumerName = payload.ConsumerFullName
            ?? Join(payload.ConsumerFirstName, payload.ConsumerLastName);
        var cardholderName = NullIfBlank(card.CardholderFullName) ?? NullIfBlank(payload.Token?.CardholderFullName)
            ?? NullIfBlank(consumerName);
        var telephone = Telephone(payload.ConsumerMobileNumber);
        var email = NullIfBlank(payload.ConsumerEmailAddress);
        var (billFirst, billLast) = SplitName(payload.BillingAddress?.Name, payload, cardholderName);
        var (shipFirst, shipLast) = SplitName(payload.ShippingAddress?.Name, payload, cardholderName);
        var isSecurityCode = payload.DynamicData?.DynamicDataType == DynamicDataTypes.DynamicSecurityCode;

        return new CompleteCheckoutResponse
        {
            CardNo = card.PrimaryAccountNumber,
            CardExpDate = year[^2..] + month.PadLeft(2, '0'),
            CardCvv2 = isSecurityCode ? NullIfBlank(payload.DynamicData?.DynamicDataValue) : null,
            CardholderName = cardholderName,
            BillToFirstName = billFirst,
            BillToLastName = billLast,
            BillToAddress1 = NullIfBlank(payload.BillingAddress?.Line1),
            BillToAddress2 = NullIfBlank(payload.BillingAddress?.Line2),
            BillToState = NullIfBlank(payload.BillingAddress?.State),
            BillToPostCode = NullIfBlank(payload.BillingAddress?.Zip),
            BillToCountry = NullIfBlank(payload.BillingAddress?.CountryCode),
            BillToTelephone = telephone,
            BillToEmail = email,
            ShipToFirstName = payload.ShippingAddress is null ? null : shipFirst,
            ShipToLastName = payload.ShippingAddress is null ? null : shipLast,
            ShipToAddress1 = NullIfBlank(payload.ShippingAddress?.Line1),
            ShipToAddress2 = NullIfBlank(payload.ShippingAddress?.Line2),
            ShipToState = NullIfBlank(payload.ShippingAddress?.State),
            ShipToPostCode = NullIfBlank(payload.ShippingAddress?.Zip),
            ShipToCountry = NullIfBlank(payload.ShippingAddress?.CountryCode),
            ShipToTelephone = payload.ShippingAddress is null ? null : telephone,
            ShipToEmail = payload.ShippingAddress is null ? null : email
        };
    }

    // The address name ("John Doe") split at the last space; without one, the consumer's first and last names.
    private static (string? First, string? Last) SplitName(string? addressName, CheckoutPanPayloadDto payload, string? fallback)
    {
        var name = NullIfBlank(addressName);
        if (name is null && (NullIfBlank(payload.ConsumerFirstName) is not null || NullIfBlank(payload.ConsumerLastName) is not null))
        {
            return (NullIfBlank(payload.ConsumerFirstName), NullIfBlank(payload.ConsumerLastName));
        }

        name ??= fallback;
        if (name is null)
        {
            return (null, null);
        }

        var space = name.LastIndexOf(' ');
        return space < 0 ? (name, null) : (name[..space].Trim(), name[(space + 1)..]);
    }

    private static string? Telephone(ConsumerMobileNumberDto? phone)
    {
        var number = new string((phone?.PhoneNumber ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        if (number.Length == 0)
        {
            return null;
        }

        var countryCode = new string((phone?.CountryCode ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        return countryCode + number;
    }

    private static string? Join(string? first, string? last) => NullIfBlank($"{first} {last}".Trim());

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
