using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MC_ClickToPay.Services.Payments;
using MC_ClickToPay.Services.Payments.PowerTranz;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MC_ClickToPay.Services.Tests;

public sealed class PowerTranzPaymentProcessorTests
{
    private static readonly PowerTranzOptions Settings = new()
    {
        BaseUrl = "https://staging.ptranz.com",
        PowerTranzId = "88800000",
        PowerTranzPassword = "secret",
        Transaction = "Auth",
    };

    [Fact]
    public async Task SendsServerToServerAuthWithoutThreeDSecureOrCvv()
    {
        var handler = new FakeHandler(HttpStatusCode.OK,
            """{"Approved":true,"IsoResponseCode":"00","ResponseMessage":"Transaction is approved","TransactionIdentifier":"tx-1","AuthorizationCode":"123456"}""");

        var result = await Processor(handler).ProcessAsync(PanRequest(), CancellationToken.None);

        Assert.True(result.Approved);
        Assert.Equal("00", result.ResponseCode);
        Assert.Equal("123456", result.AuthorizationCode);
        Assert.Equal("tx-1", result.TransactionId);

        var (uri, headers, body) = Assert.Single(handler.Requests);
        Assert.Equal("https://staging.ptranz.com/Api/Auth", uri);
        Assert.Equal("88800000", headers["PowerTranz-PowerTranzId"]);
        Assert.Equal("secret", headers["PowerTranz-PowerTranzPassword"]);

        var json = JsonNode.Parse(body)!;
        Assert.False(json["ThreeDSecure"]!.GetValue<bool>());
        Assert.Equal("840", json["CurrencyCode"]!.GetValue<string>());
        Assert.Equal(31.25m, json["TotalAmount"]!.GetValue<decimal>());
        Assert.Equal("ORDER-1", json["OrderIdentifier"]!.GetValue<string>());
        Assert.Equal("5120350100064537", json["Source"]!["CardPan"]!.GetValue<string>());
        Assert.Equal("2812", json["Source"]!["CardExpiration"]!.GetValue<string>());
        Assert.Null(json["Source"]!["CardCvv"]);
        Assert.Equal("840", json["BillingAddress"]!["CountryCode"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReturnsDeclinesAsResults()
    {
        var handler = new FakeHandler(HttpStatusCode.OK,
            """{"Approved":false,"IsoResponseCode":"05","ResponseMessage":"Do not honour","TransactionIdentifier":"tx-2"}""");

        var result = await Processor(handler).ProcessAsync(PanRequest(), CancellationToken.None);

        Assert.False(result.Approved);
        Assert.Equal("05", result.ResponseCode);
    }

    [Fact]
    public async Task ReportsValidationErrorsAsFailuresWithoutCardData()
    {
        var handler = new FakeHandler(HttpStatusCode.OK,
            """{"Approved":false,"IsoResponseCode":"12","ResponseMessage":"Invalid transaction","Errors":[{"Code":"76","Message":"Invalid card"}]}""");

        var exception = await Assert.ThrowsAsync<PaymentProcessingException>(() =>
            Processor(handler).ProcessAsync(PanRequest(), CancellationToken.None));

        Assert.Contains("[76] Invalid card", exception.Message);
        Assert.DoesNotContain("5120350100064537", exception.Message);
    }

    [Fact]
    public async Task AddsCryptogramAndEciForNetworkTokensWhenFieldsAreConfigured()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """{"Approved":true,"IsoResponseCode":"00","ResponseMessage":"OK"}""");
        var settings = new PowerTranzOptions
        {
            BaseUrl = Settings.BaseUrl,
            PowerTranzId = Settings.PowerTranzId,
            PowerTranzPassword = Settings.PowerTranzPassword,
            CryptogramSourceField = "Cryptogram",
            EciSourceField = "Eci",
        };
        var request = new PaymentRequest
        {
            OrderId = "ORDER-2",
            Amount = 6m,
            CurrencyCode = "978",
            CredentialType = PaymentCredentialType.NetworkToken,
            AccountNumber = "5185600649952671",
            Expiration = "2901",
            Cryptogram = "AN5mxS9FCZE2ACfVeZZbAAADFA==",
            Eci = "06",
        };

        await Processor(handler, settings).ProcessAsync(request, CancellationToken.None);

        var source = JsonNode.Parse(Assert.Single(handler.Requests).Body)!["Source"]!;
        Assert.Equal("AN5mxS9FCZE2ACfVeZZbAAADFA==", source["Cryptogram"]!.GetValue<string>());
        Assert.Equal("06", source["Eci"]!.GetValue<string>());
    }

    [Fact]
    public async Task SendsDynamicSecurityCodeAsCardCvv()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """{"Approved":true,"IsoResponseCode":"00","ResponseMessage":"OK"}""");
        var request = new PaymentRequest
        {
            OrderId = "ORDER-3",
            Amount = 31.25m,
            CurrencyCode = "USD",
            CredentialType = PaymentCredentialType.Pan,
            AccountNumber = "5120350100064537",
            Expiration = "3012",
            SecurityCode = "637",
            CryptogramType = "DYNAMIC_CARD_SECURITY_CODE",
        };

        await Processor(handler).ProcessAsync(request, CancellationToken.None);

        var source = JsonNode.Parse(Assert.Single(handler.Requests).Body)!["Source"]!;
        Assert.Equal("637", source["CardCvv"]!.GetValue<string>());
        Assert.Equal("5120350100064537", source["CardPan"]!.GetValue<string>());
    }

    [Fact]
    public async Task AddsCryptogramForDsrpPlusPanWhenFieldIsConfigured()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """{"Approved":true,"IsoResponseCode":"00","ResponseMessage":"OK"}""");
        var settings = new PowerTranzOptions
        {
            BaseUrl = Settings.BaseUrl,
            PowerTranzId = Settings.PowerTranzId,
            PowerTranzPassword = Settings.PowerTranzPassword,
            CryptogramSourceField = "Cryptogram",
        };
        var request = new PaymentRequest
        {
            OrderId = "ORDER-4",
            Amount = 31.25m,
            CurrencyCode = "USD",
            CredentialType = PaymentCredentialType.Pan,
            AccountNumber = "5120350100064537",
            Expiration = "2907",
            Cryptogram = "AH14E2rQmy6mABQkMkPpAAADFA==",
            CryptogramType = "CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM",
        };

        await Processor(handler, settings).ProcessAsync(request, CancellationToken.None);

        var source = JsonNode.Parse(Assert.Single(handler.Requests).Body)!["Source"]!;
        Assert.Equal("AH14E2rQmy6mABQkMkPpAAADFA==", source["Cryptogram"]!.GetValue<string>());
        Assert.Null(source["CardCvv"]);
    }

    private static PaymentRequest PanRequest() => new()
    {
        OrderId = "ORDER-1",
        Amount = 31.25m,
        CurrencyCode = "USD",
        CredentialType = PaymentCredentialType.Pan,
        AccountNumber = "5120350100064537",
        Expiration = "2812",
        CardholderName = "john doe",
        BillingAddress = new PaymentBillingAddress { City = "New York City", CountryCode = "US" },
    };

    private static PowerTranzPaymentProcessor Processor(FakeHandler handler, PowerTranzOptions? settings = null) => new(
        new HttpClient(handler), Options.Create(settings ?? Settings), TimeProvider.System,
        NullLogger<PowerTranzPaymentProcessor>.Instance);

    private sealed class FakeHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public List<(string Uri, Dictionary<string, string> Headers, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((
                request.RequestUri!.ToString(),
                request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value)),
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(status) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") };
        }
    }
}
