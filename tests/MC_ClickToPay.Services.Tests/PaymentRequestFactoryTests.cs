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

        Assert.Equal("2901", request.TokenExpiration);
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
        Assert.Equal("5480983179133165", request.NetworkToken);
        Assert.Equal("3007", request.TokenExpiration);
        Assert.Equal("cryptogram", request.Cryptogram);
        Assert.Equal("PAR-1", request.PaymentAccountReference);
        Assert.Equal("Jane Demo", request.CardholderName);
        Assert.Equal("3165", request.TokenLast4);
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
        var payload = new DecryptedPayloadDto
        {
            Token = new PaymentTokenDto { PaymentToken = "5480983179133165", TokenExpirationMonth = "7", TokenExpirationYear = "30" },
            DynamicData = Cryptogram(),
            ConsumerFullName = "John Q Public",
            ShippingAddress = new PaymentAddressDto { City = "Kingston", CountryCode = "JM" }
        };

        var request = Factory.Create(payload, 1m, "388", "ORDER-2");

        Assert.Equal("3007", request.TokenExpiration);
        Assert.Equal("John Q Public", request.CardholderName);
        Assert.Equal("John Q", request.BillingAddress!.FirstName);
        Assert.Equal("Public", request.BillingAddress.LastName);
        Assert.Equal("Kingston", request.BillingAddress.City);
    }

    [Fact]
    public void AcceptsTokenExpiringThisMonth()
    {
        var payload = Payload(month: "10", year: "2026");

        Assert.Equal("2610", Factory.Create(payload, 1m, "USD", "ORDER-3").TokenExpiration);
    }

    [Theory]
    [InlineData("1234", "07", "2030", "cryptogram", "CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM", "token.paymentToken")]
    [InlineData("5480abc179133165", "07", "2030", "cryptogram", "CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM", "token.paymentToken")]
    [InlineData("5480983179133165", "13", "2030", "cryptogram", "CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM", "token.tokenExpirationMonth")]
    [InlineData("5480983179133165", "07", "203", "cryptogram", "CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM", "token.tokenExpirationYear")]
    [InlineData("5480983179133165", "09", "2026", "cryptogram", "CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM", "token is expired")]
    [InlineData("5480983179133165", "07", "2030", " ", "CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM", "dynamicData.dynamicDataValue")]
    [InlineData("5480983179133165", "07", "2030", "cryptogram", "TAVV", "dynamicData.dynamicDataType")]
    public void RejectsInvalidPaymentDataWithoutEchoingValues(
        string token, string month, string year, string cryptogram, string cryptogramType, string expectedError)
    {
        var payload = new DecryptedPayloadDto
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
        var payload = new DecryptedPayloadDto { Token = new PaymentTokenDto(), DynamicData = new DynamicDataDto() };

        var ex = Assert.Throws<InvalidPaymentDataException>(() => Factory.Create(payload, 1m, "USD", "ORDER-5"));

        Assert.Equal(5, ex.Errors.Count);
    }

    private static DecryptedPayloadDto Payload(string month = "07", string year = "2030") => new()
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
