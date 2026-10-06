using MC_ClickToPay.Services.Payments;
using MC_ClickToPay.Services.Models;
using Xunit;

namespace MC_ClickToPay.Services.Tests;

public sealed class PaymentRequestFactoryTests
{
    private static readonly PaymentRequestFactory Factory =
        new(new FixedTimeProvider(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero)));

    // Same shape as a real Mastercard sandbox payload (2026-10-01), with fake values: no consumer names, the name
    // only in token.cardholderFullName, billing + shipping addresses and a mobile number.
    private const string SandboxShapedPayload = """
        {"consumerEmailAddress":"jane.demo@example.com",
         "dynamicData":{"dynamicDataValue":"DEMOcryptogramNOTREAL000000=","dynamicDataType":"CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM"},
         "shippingAddress":{"zip":"10038","city":"New York","countryCode":"US","name":"Jane Demo","state":"NY","line1":"456 Demo Avenue"},
         "billingAddress":{"zip":"99950","city":"Denver","countryCode":"US","state":"CO","line2":"Apt 2","line1":"123 Demo Street"},
         "consumerMobileNumber":{"phoneNumber":"5550100","countryCode":"1"},
         "token":{"paymentToken":"5480983179133165","paymentAccountReference":"DEMO0000000000000000000000001",
                  "tokenExpirationMonth":"01","cardholderFullName":"Jane Demo","tokenExpirationYear":"2029"}}
        """;

    [Fact]
    public void MapsMastercardSandboxShapedPayloadWithEci()
    {
        var payload = System.Text.Json.JsonSerializer.Deserialize<DecryptedPayloadDto>(SandboxShapedPayload)!;

        var request = Factory.Create(payload, 31.25m, "USD", "ORDER-5", eci: "06");

        Assert.Equal("2901", request.Expiration);
        Assert.Equal("06", request.Eci);
        Assert.Equal("Jane Demo", request.CardholderName);
        Assert.Equal("Jane", request.BillingAddress!.FirstName);
        Assert.Equal("Demo", request.BillingAddress.LastName);
        Assert.Equal("Denver", request.BillingAddress.City);
        Assert.Equal("Apt 2", request.BillingAddress.Line2);
        Assert.Equal("15550100", request.BillingAddress.PhoneNumber);
        Assert.Equal("jane.demo@example.com", request.BillingAddress.EmailAddress);
    }

    [Fact]
    public void MapsDecryptedPayloadToPaymentRequest()
    {
        var request = Factory.Create(Payload(), 6.004m, "USD", "ORDER-1");

        Assert.Equal("ORDER-1", request.OrderId);
        Assert.Equal(6.00m, request.Amount);
        Assert.Equal("USD", request.CurrencyCode);
        Assert.Equal("5480983179133165", request.AccountNumber);
        Assert.Equal("3007", request.Expiration);
        Assert.Equal("cryptogram", request.Cryptogram);
        Assert.Equal("PAR-1", request.PaymentAccountReference);
        Assert.Equal("Jane Demo", request.CardholderName);
        Assert.Equal("3165", request.Last4);
        var billing = request.BillingAddress!;
        Assert.Equal("Jane", billing.FirstName);
        Assert.Equal("Demo", billing.LastName);
        Assert.Equal("123 Demo Street", billing.Line1);
        Assert.Equal("10001", billing.PostalCode);
        Assert.Equal("US", billing.CountryCode);
        Assert.Equal("jane@example.com", billing.EmailAddress);
        Assert.Equal("15550100", billing.PhoneNumber);
    }

    [Fact]
    public void FallsBackToConsumerFullNameAndShippingAddress()
    {
        var payload = new TokenizedPayloadDto
        {
            Token = new PaymentTokenDto { PaymentToken = "5480983179133165", TokenExpirationMonth = "7", TokenExpirationYear = "30" },
            DynamicData = Cryptogram(),
            ConsumerFullName = "John Q Public",
            ShippingAddress = new PaymentAddressDto { City = "Kingston", CountryCode = "JM" }
        };

        var request = Factory.Create(payload, 1m, "388", "ORDER-2");

        Assert.Equal("3007", request.Expiration);
        Assert.Equal("John Q Public", request.CardholderName);
        Assert.Equal("John Q", request.BillingAddress!.FirstName);
        Assert.Equal("Public", request.BillingAddress.LastName);
        Assert.Equal("Kingston", request.BillingAddress.City);
    }

    [Fact]
    public void AcceptsTokenExpiringThisMonth()
    {
        var payload = Payload(month: "10", year: "2026");

        Assert.Equal("2610", Factory.Create(payload, 1m, "USD", "ORDER-3").Expiration);
    }

    [Theory]
    [InlineData("1234", "07", "2030", "cryptogram", "CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM", "token.paymentToken")]
    [InlineData("5480abc179133165", "07", "2030", "cryptogram", "CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM", "token.paymentToken")]
    [InlineData("5480983179133165", "13", "2030", "cryptogram", "CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM", "token.tokenExpirationMonth")]
    [InlineData("5480983179133165", "07", "203", "cryptogram", "CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM", "token.tokenExpirationYear")]
    [InlineData("5480983179133165", "09", "2026", "cryptogram", "CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM", "token is expired")]
    [InlineData("5480983179133165", "07", "2030", " ", "CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM", "dynamicData.dynamicDataValue")]
    public void RejectsInvalidPaymentDataWithoutEchoingValues(
        string token, string month, string year, string cryptogram, string cryptogramType, string expectedError)
    {
        var payload = new TokenizedPayloadDto
        {
            Token = new PaymentTokenDto { PaymentToken = token, TokenExpirationMonth = month, TokenExpirationYear = year },
            DynamicData = new DynamicDataDto { DynamicDataValue = cryptogram, DynamicDataType = cryptogramType }
        };

        var ex = Assert.Throws<InvalidPaymentDataException>(() => Factory.Create(payload, 1m, "USD", "ORDER-4"));

        var error = Assert.Single(ex.Errors);
        Assert.StartsWith(expectedError, error);
        Assert.DoesNotContain(token, string.Join(' ', ex.Errors) + ex.Message);
    }

    [Fact]
    public void ReportsEveryInvalidFieldAtOnce()
    {
        var payload = new TokenizedPayloadDto
        {
            Token = new PaymentTokenDto { PaymentToken = "not-digits" },
            DynamicData = new DynamicDataDto()
        };

        var ex = Assert.Throws<InvalidPaymentDataException>(() => Factory.Create(payload, 1m, "USD", "ORDER-5"));

        // Token digits, expiry month, expiry year and cryptogram.
        Assert.Equal(4, ex.Errors.Count);
    }

    // The Mastercard FPAN payload (dynamicDataType NONE, card object), as documented, with a future expiry.
    private const string FpanPayload = """
        {"consumerEmailAddress":"test1234@dummyemail.com",
         "dynamicData":{"dynamicDataType":"NONE"},
         "billingAddress":{"zip":"10011","city":"New York City","countryCode":"US","state":"NY","line2":"Floor 10","line1":"100 5th Avenue"},
         "consumerMobileNumber":{"phoneNumber":"9912793770","countryCode":"1"},
         "card":{"cardholderFullName":"john doe","panExpirationYear":"2030","primaryAccountNumber":"5120350100064537","panExpirationMonth":"12"}}
        """;

    [Fact]
    public void MapsFpanPayloadWithoutCryptogram()
    {
        var payload = System.Text.Json.JsonSerializer.Deserialize<DecryptedPayloadDto>(FpanPayload)!;

        var request = Factory.Create(payload, 31.25m, "USD", "ORDER-6", eci: "06");

        Assert.Equal(PaymentCredentialType.Pan, request.CredentialType);
        Assert.Equal("5120350100064537", request.AccountNumber);
        Assert.Equal("3012", request.Expiration);
        Assert.Null(request.Cryptogram);
        Assert.Equal("NONE", request.CryptogramType);
        Assert.Equal("4537", request.Last4);
        Assert.Equal("john doe", request.CardholderName);
        Assert.Equal("john", request.BillingAddress!.FirstName);
        Assert.Equal("doe", request.BillingAddress.LastName);
        Assert.Equal("Floor 10", request.BillingAddress.Line2);
        Assert.Equal("19912793770", request.BillingAddress.PhoneNumber);
    }

    [Fact]
    public void RejectsExpiredFpanAsInTheMastercardDocsExample()
    {
        var payload = System.Text.Json.JsonSerializer.Deserialize<DecryptedPayloadDto>(
            FpanPayload.Replace("\"2030\"", "\"2022\""))!;

        var ex = Assert.Throws<InvalidPaymentDataException>(() => Factory.Create(payload, 1m, "USD", "ORDER-7"));

        Assert.StartsWith("card is expired", Assert.Single(ex.Errors));
        Assert.DoesNotContain("5120350100064537", string.Join(' ', ex.Errors));
    }

    [Fact]
    public void PrefersTokenInDualPayload()
    {
        var payload = Payload();
        var dual = new DsrpPanPayloadDto
        {
            Token = payload.Token,
            DynamicData = payload.DynamicData,
            Card = new PaymentCardDto { PrimaryAccountNumber = "5120350100064537", PanExpirationMonth = "12", PanExpirationYear = "2030" }
        };

        var request = Factory.Create(dual, 1m, "USD", "ORDER-8");

        Assert.Equal(PaymentCredentialType.NetworkToken, request.CredentialType);
        Assert.Equal("3165", request.Last4);
    }

    // Mastercard "PAN (token only)": card + Dynamic Token Verification Code (docs example, future expiry).
    private const string PanWithDtvcPayload = """
        {"consumerEmailAddress":"test1234@dummyemail.com",
         "dynamicData":{"dynamicDataValue":"637","dynamicDataType":"DYNAMIC_CARD_SECURITY_CODE"},
         "billingAddress":{"zip":"10011","city":"New York City","countryCode":"US","state":"NY","line2":"Floor 10","line1":"100 5th Avenue"},
         "consumerMobileNumber":{"phoneNumber":"9912793770","countryCode":"1"},
         "card":{"cardholderFullName":"john doe","panExpirationYear":"2030","primaryAccountNumber":"5120350100064537","panExpirationMonth":"12"}}
        """;

    // Mastercard "DSRP + PAN": DSRP cryptogram in dynamicData, PAN in card, masked token (docs example).
    private const string DsrpPlusPanPayload = """
        {"card":{"primaryAccountNumber":"5120350100064537","panExpirationMonth":"07","panExpirationYear":"2029","cardholderFullName":"John Doe"},
         "token":{"paymentToken":"************9541","tokenExpirationMonth":"08","tokenExpirationYear":"2027",
                  "paymentAccountReference":"5001EN20X29ADV3HFKSFQS6KDII9D","cardholderFullName":"John Doe"},
         "shippingAddress":{"name":"John Doe","line1":"150 5th Avenue","city":"New York","state":"NY","countryCode":"US","zip":"10011"},
         "consumerEmailAddress":"john.doe@mastercard.com",
         "consumerMobileNumber":{"countryCode":"44","phoneNumber":"7966778607"},
         "dynamicData":{"dynamicDataValue":"AH14E2rQmy6mABQkMkPpAAADFA==","dynamicDataType":"CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM"},
         "billingAddress":{"line1":"150 5th Avenue","city":"New York","state":"NY","countryCode":"US","zip":"10011"}}
        """;

    [Fact]
    public void MapsPanWithDynamicSecurityCode()
    {
        var payload = System.Text.Json.JsonSerializer.Deserialize<DecryptedPayloadDto>(PanWithDtvcPayload)!;

        var request = Factory.Create(payload, 31.25m, "USD", "ORDER-10");

        Assert.Equal(PaymentCredentialType.Pan, request.CredentialType);
        Assert.Equal("5120350100064537", request.AccountNumber);
        Assert.Equal("3012", request.Expiration);
        Assert.Equal("637", request.SecurityCode);
        Assert.Null(request.Cryptogram);
        Assert.Equal("DYNAMIC_CARD_SECURITY_CODE", request.CryptogramType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("63")]
    [InlineData("6a7")]
    public void RejectsInvalidDynamicSecurityCodeWithoutEchoingIt(string code)
    {
        var payload = System.Text.Json.JsonSerializer.Deserialize<DecryptedPayloadDto>(
            PanWithDtvcPayload.Replace("\"637\"", $"\"{code}\""))!;

        var ex = Assert.Throws<InvalidPaymentDataException>(() => Factory.Create(payload, 1m, "USD", "ORDER-11"));

        Assert.StartsWith("dynamicData.dynamicDataValue (dynamic security code)", Assert.Single(ex.Errors));
    }

    [Fact]
    public void MapsDsrpPlusPanUsingThePanAndTheCryptogram()
    {
        var payload = System.Text.Json.JsonSerializer.Deserialize<DecryptedPayloadDto>(DsrpPlusPanPayload)!;

        var request = Factory.Create(payload, 31.25m, "USD", "ORDER-12", eci: "02");

        Assert.Equal(PaymentCredentialType.Pan, request.CredentialType);
        Assert.Equal("5120350100064537", request.AccountNumber);
        Assert.Equal("2907", request.Expiration);
        Assert.Equal("AH14E2rQmy6mABQkMkPpAAADFA==", request.Cryptogram);
        Assert.Null(request.SecurityCode);
        Assert.Equal("CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM", request.CryptogramType);
        Assert.Equal("5001EN20X29ADV3HFKSFQS6KDII9D", request.PaymentAccountReference);
        Assert.Equal("John Doe", request.CardholderName);
        Assert.Equal("4537", request.Last4);
    }

    [Fact]
    public void RejectsMaskedTokenWithoutCard()
    {
        var payload = new TokenizedPayloadDto
        {
            Token = new PaymentTokenDto { PaymentToken = "************9541", TokenExpirationMonth = "08", TokenExpirationYear = "2027" },
            DynamicData = Cryptogram()
        };

        var ex = Assert.Throws<InvalidPaymentDataException>(() => Factory.Create(payload, 1m, "USD", "ORDER-13"));

        Assert.StartsWith("token.paymentToken", Assert.Single(ex.Errors));
    }

    [Fact]
    public void RequiresTheCredentialOfEachModel()
    {
        var tokenError = Assert.Throws<InvalidPaymentDataException>(
            () => Factory.Create(new TokenizedPayloadDto { DynamicData = Cryptogram() }, 1m, "USD", "ORDER-9"));
        var cardError = Assert.Throws<InvalidPaymentDataException>(
            () => Factory.Create(new FpanPayloadDto(), 1m, "USD", "ORDER-9"));

        Assert.Equal("token.paymentToken is required.", Assert.Single(tokenError.Errors));
        Assert.Equal("card.primaryAccountNumber is required.", Assert.Single(cardError.Errors));
    }

    [Theory]
    [InlineData("""{"token":{"paymentToken":"1"},"dynamicData":{"dynamicDataType":"CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM"}}""", typeof(TokenizedPayloadDto))]
    [InlineData("""{"card":{"primaryAccountNumber":"1"},"token":{"paymentToken":"*1"},"dynamicData":{"dynamicDataType":"CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM"}}""", typeof(DsrpPanPayloadDto))]
    [InlineData("""{"card":{"primaryAccountNumber":"1"},"dynamicData":{"dynamicDataType":"DYNAMIC_CARD_SECURITY_CODE"}}""", typeof(DynamicSecurityCodePayloadDto))]
    [InlineData("""{"card":{"primaryAccountNumber":"1"},"dynamicData":{"dynamicDataType":"NONE"}}""", typeof(FpanPayloadDto))]
    [InlineData("""{"card":{"primaryAccountNumber":"1"}}""", typeof(FpanPayloadDto))]
    public void DeserializesTheModelChosenByDynamicDataType(string json, Type expected)
    {
        var payload = System.Text.Json.JsonSerializer.Deserialize<DecryptedPayloadDto>(json);

        Assert.IsType(expected, payload);
    }

    [Fact]
    public void RejectsUnknownDynamicDataTypeWithoutEchoingIt()
    {
        var ex = Assert.Throws<System.Text.Json.JsonException>(() =>
            System.Text.Json.JsonSerializer.Deserialize<DecryptedPayloadDto>("""{"dynamicData":{"dynamicDataType":"TAVV-SECRET"}}"""));

        Assert.DoesNotContain("TAVV-SECRET", ex.Message);
    }

    [Fact]
    public void SerializesOnlyTheFieldsOfTheConcreteModel()
    {
        DecryptedPayloadDto payload = new FpanPayloadDto
        {
            Card = new PaymentCardDto { PrimaryAccountNumber = "5120350100064537" },
            DynamicData = new DynamicDataDto { DynamicDataType = "NONE" }
        };

        var json = System.Text.Json.JsonSerializer.Serialize(payload);

        Assert.Contains("\"card\"", json);
        Assert.DoesNotContain("\"token\"", json);
        Assert.DoesNotContain("Credential", json);
    }

    private static TokenizedPayloadDto Payload(string month = "07", string year = "2030") => new()
    {
        Token = new PaymentTokenDto
        {
            PaymentToken = "5480983179133165",
            TokenExpirationMonth = month,
            TokenExpirationYear = year,
            PaymentAccountReference = "PAR-1",
            CardholderFullName = "Jane Demo"
        },
        DynamicData = Cryptogram(),
        ConsumerFirstName = "Jane",
        ConsumerLastName = "Demo",
        ConsumerEmailAddress = "jane@example.com",
        ConsumerMobileNumber = new ConsumerMobileNumberDto { CountryCode = "1", PhoneNumber = "5550100" },
        BillingAddress = new PaymentAddressDto { Line1 = "123 Demo Street", City = "New York", Zip = "10001", CountryCode = "US" }
    };

    private static DynamicDataDto Cryptogram() => new()
    {
        DynamicDataValue = "cryptogram",
        DynamicDataType = PaymentRequestFactory.SupportedCryptogramType
    };
}
