using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MC_ClickToPay.Services.Abstractions;
using MC_ClickToPay.Services.Checkout;
using MC_ClickToPay.Services.Exceptions;
using MC_ClickToPay.Services.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MC_ClickToPay.Api.Tests;

public sealed class CompleteCheckoutEndpointTests
{
    private const string Route = "/api/checkout";

    private static readonly object FrontRequest = new
    {
        spiToken = "spi-token-1",
        srcDpaId = "823ef281-1a2e-4204-a69c-43d355522a35",
        srcCorrelationId = "34f4a04b.2ce61515-55b7-4c09-845b-45492c2ef327",
        merchantTransactionId = "0a4e0d3.34f4a04b.f91587770b3c99ce1090fe8a4f2a931b10d7acfc",
        flowId = "34f4a04b.2ce61515-55b7-4c09-845b-45492c2ef327.1790888627",
        transactionAmount = 31.25m,
        transactionCurrencyCode = "USD",
        xCorrelationId = "7d1f6c2e-9a43-4b8e-b1a2-3f5c8e0d4a17"
    };

    [Fact]
    public async Task DsrpPanPayloadIsReturnedAsFlatFields()
    {
        var decrypted = """
            {"card":{"primaryAccountNumber":"5120350100064537","panExpirationMonth":"7","panExpirationYear":"2029","cardholderFullName":"John Doe"},
             "token":{"paymentToken":"************9541"},
             "dynamicData":{"dynamicDataValue":"AH14E2rQmy6mABQkMkPpAAADFA==","dynamicDataType":"CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM"},
             "billingAddress":{"name":"John Doe","line1":"150 5th Avenue","city":"New York","state":"NY","countryCode":"US","zip":"10011"},
             "shippingAddress":{"name":"Mary Ann Smith","line1":"1 Main St","line2":"Apt 2","state":"CA","countryCode":"US","zip":"90001"},
             "consumerEmailAddress":"john.doe@example.com",
             "consumerMobileNumber":{"countryCode":"1","phoneNumber":"212-555-0100"}}
            """;

        var body = await PostAsync(decrypted);

        Assert.Equal("5120350100064537", body.GetProperty("cardNo").GetString());
        Assert.Equal("2907", body.GetProperty("cardExpDate").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("cardCVV2").ValueKind);
        Assert.Equal("John Doe", body.GetProperty("cardholderName").GetString());
        Assert.Equal("John", body.GetProperty("billToFirstName").GetString());
        Assert.Equal("Doe", body.GetProperty("billToLastName").GetString());
        Assert.Equal("150 5th Avenue", body.GetProperty("billToAddress1").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("billToAddress2").ValueKind);
        Assert.Equal("NY", body.GetProperty("billToState").GetString());
        Assert.Equal("10011", body.GetProperty("billToPostCode").GetString());
        Assert.Equal("US", body.GetProperty("billToCountry").GetString());
        Assert.Equal("12125550100", body.GetProperty("billToTelephone").GetString());
        Assert.Equal("john.doe@example.com", body.GetProperty("billToEmail").GetString());
        Assert.Equal("Mary Ann", body.GetProperty("shipToFirstName").GetString());
        Assert.Equal("Smith", body.GetProperty("shipToLastName").GetString());
        Assert.Equal("Apt 2", body.GetProperty("shipToAddress2").GetString());
        Assert.Equal("90001", body.GetProperty("shipToPostCode").GetString());

        // The cryptogram and token are not part of the flat response.
        Assert.DoesNotContain("AH14E2rQmy6mABQkMkPpAAADFA==", body.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DynamicSecurityCodeIsReturnedAsCvv2()
    {
        var body = await PostAsync("""{"card":{"primaryAccountNumber":"5120350100064537","panExpirationMonth":"12","panExpirationYear":"2030"},"dynamicData":{"dynamicDataValue":"637","dynamicDataType":"DYNAMIC_CARD_SECURITY_CODE"}}""");

        Assert.Equal("5120350100064537", body.GetProperty("cardNo").GetString());
        Assert.Equal("3012", body.GetProperty("cardExpDate").GetString());
        Assert.Equal("637", body.GetProperty("cardCVV2").GetString());
    }

    [Fact]
    public async Task FpanWithoutAddressesHasNullBillToAndShipTo()
    {
        var body = await PostAsync("""{"card":{"primaryAccountNumber":"5120350100064537","panExpirationMonth":"12","panExpirationYear":"2030"},"dynamicData":{"dynamicDataType":"NONE"}}""");

        Assert.Equal("5120350100064537", body.GetProperty("cardNo").GetString());
        foreach (var field in new[] { "cardCVV2", "cardholderName", "billToFirstName", "billToAddress1", "billToCountry", "billToTelephone", "billToEmail", "shipToFirstName", "shipToAddress1", "shipToEmail" })
        {
            Assert.Equal(JsonValueKind.Null, body.GetProperty(field).ValueKind);
        }
    }

    [Fact]
    public async Task TokenOnlyPayloadReturnsTheTokenAsCardNo()
    {
        var body = await PostAsync("""{"token":{"paymentToken":"5186051929138756","tokenExpirationMonth":"09","tokenExpirationYear":"2029","cardholderFullName":"Adelle Ryan"},"dynamicData":{"dynamicDataValue":"AAnIdMIzlaW+ACJyvravAAADFA==","dynamicDataType":"CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM"}}""");

        Assert.Equal("5186051929138756", body.GetProperty("cardNo").GetString());
        Assert.Equal("2909", body.GetProperty("cardExpDate").GetString());
        Assert.Equal("Adelle Ryan", body.GetProperty("cardholderName").GetString());
    }

    // The four payload types Mastercard sends, as shared by the team (sandbox test data).
    [Theory]
    [InlineData("""{"consumerEmailAddress":"test1234@dummyemail.com","dynamicData":{"dynamicDataValue":"22233445566778899001122334455667788990011223344556677889900112233445566778899001122 3344556677889900","dynamicDataType":"CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM"},"billingAddress":{"zip":"10011","city":"New York City","countryCode":"US","state":"NY","line2":"Floor 10","line1":"100 5th Avenue"},"consumerMobileNumber":{"phoneNumber":"9110717013","countryCode":"1"},"srcTokenResultsData":{"unpredictableNumber":"c44f37b5"},"token":{"paymentToken":"5455030208766644","paymentAccountReference":"5001a9f027e5629d11e3949a0800b","tokenExpirationMonth":"07","cardholderFullName":"john doe","tokenExpirationYear":"2026"}}""",
        "5455030208766644", "2607", null, "john doe", "19110717013", "Floor 10", null)]
    [InlineData("""{"consumerEmailAddress":"test1234@dummyemail.com","dynamicData":{"dynamicDataType":"NONE"},"billingAddress":{"zip":"10011","city":"New York City","countryCode":"US","state":"NY","line2":"Floor 10","line1":"100 5th Avenue"},"consumerMobileNumber":{"phoneNumber":"9912793770","countryCode":"1"},"card":{"cardholderFullName":"john doe","panExpirationYear":"2022","primaryAccountNumber":"5120350100064537","panExpirationMonth":"12"}}""",
        "5120350100064537", "2212", null, "john doe", "19912793770", "Floor 10", null)]
    [InlineData("""{"consumerEmailAddress":"test1234@dummyemail.com","dynamicData":{"dynamicDataValue":"637","dynamicDataType":"DYNAMIC_CARD_SECURITY_CODE"},"billingAddress":{"zip":"10011","city":"New York City","countryCode":"US","state":"NY","line2":"Floor 10","line1":"100 5th Avenue"},"consumerMobileNumber":{"phoneNumber":"9912793770","countryCode":"1"},"card":{"cardholderFullName":"john doe","panExpirationYear":"2030","primaryAccountNumber":"5120350100064537","panExpirationMonth":"12"}}""",
        "5120350100064537", "3012", "637", "john doe", "19912793770", "Floor 10", null)]
    [InlineData("""{"card":{"primaryAccountNumber":"5120350100064537","panExpirationMonth":"07","panExpirationYear":"2029","cardholderFullName":"John Doe"},"token":{"paymentToken":"************9541","tokenExpirationMonth":"08","tokenExpirationYear":"2027","paymentAccountReference":"5001EN20X29ADV3HFKSFQS6KDII9D","cardholderFullName":"John Doe"},"shippingAddress":{"name":"John Doe","line1":"150 5th Avenue","city":"New York","state":"NY","countryCode":"US","zip":"10011"},"consumerEmailAddress":"john.doe@mastercard.com","consumerMobileNumber":{"countryCode":"44","phoneNumber":"7966778607"},"dynamicData":{"dynamicDataValue":"AH14E2rQmy6mABQkMkPpAAADFA==","dynamicDataType":"CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM"},"billingAddress":{"line1":"150 5th Avenue","city":"New York","state":"NY","countryCode":"US","zip":"10011"}}""",
        "5120350100064537", "2907", null, "John Doe", "447966778607", null, "150 5th Avenue")]
    public async Task AllMastercardPayloadTypesAreMapped(
        string decrypted, string cardNo, string cardExpDate, string? cvv2, string holder, string telephone,
        string? billToAddress2, string? shipToAddress1)
    {
        var body = await PostAsync(decrypted);

        Assert.Equal(cardNo, body.GetProperty("cardNo").GetString());
        Assert.Equal(cardExpDate, body.GetProperty("cardExpDate").GetString());
        Assert.Equal(cvv2, body.GetProperty("cardCVV2").GetString());
        Assert.Equal(holder, body.GetProperty("cardholderName").GetString());
        Assert.Equal(telephone, body.GetProperty("billToTelephone").GetString());
        Assert.Equal(billToAddress2, body.GetProperty("billToAddress2").GetString());
        Assert.Equal(shipToAddress1, body.GetProperty("shipToAddress1").GetString());
        Assert.Equal("NY", body.GetProperty("billToState").GetString());
        Assert.Equal("10011", body.GetProperty("billToPostCode").GetString());
        Assert.Equal("US", body.GetProperty("billToCountry").GetString());
    }

    [Fact]
    public async Task PayloadWithoutExpiryIsBadGatewayWithoutCardData()
    {
        var checkout = new FakeCheckout(new CompleteCheckoutResult
        {
            Payload = JsonSerializer.Deserialize<DecryptedPayloadDto>("""{"card":{"primaryAccountNumber":"5120350100064537"},"dynamicData":{"dynamicDataType":"NONE"}}""")!
        });
        using var baseFactory = new PayloadApiFactory();
        using var factory = Factory(baseFactory, checkout);
        using var client = Client(factory);

        using var response = await client.PostAsJsonAsync(Route, FrontRequest);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain("5120350100064537", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MastercardFailureIsBadGatewayWithoutPayloadData()
    {
        using var baseFactory = new PayloadApiFactory();
        using var factory = Factory(baseFactory, new FakeCheckout(new MastercardCheckoutException("Mastercard /srci/api/checkout failed with HTTP 400. [INVALID_ARGUMENT] Expired", 400)));
        using var client = Client(factory);

        using var response = await client.PostAsJsonAsync(Route, FrontRequest);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task RequiresTheFrontFields()
    {
        using var baseFactory = new PayloadApiFactory();
        using var factory = Factory(baseFactory, new FakeCheckout(new InvalidOperationException("must not be called")));
        using var client = Client(factory);

        using var response = await client.PostAsJsonAsync(Route, new { merchantTransactionId = "mtid-1" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0, "USD")]
    [InlineData(-1, "USD")]
    [InlineData(10.005, "USD")]
    [InlineData(10, "usd")]
    [InlineData(10, "840")]
    [InlineData(10, "")]
    public async Task RejectsAnInvalidAmountOrCurrency(double amount, string currency)
    {
        using var baseFactory = new PayloadApiFactory();
        using var factory = Factory(baseFactory, new FakeCheckout(new InvalidOperationException("must not be called")));
        using var client = Client(factory);

        using var response = await client.PostAsJsonAsync(Route, new
        {
            spiToken = "spi-token-1",
            srcDpaId = "823ef281-1a2e-4204-a69c-43d355522a35",
            srcCorrelationId = "corr-1",
            merchantTransactionId = "mtid-1",
            flowId = "flow-1",
            transactionAmount = (decimal)amount,
            transactionCurrencyCode = currency,
            xCorrelationId = "x-corr-1"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RejectsAnXCorrelationIdThatCannotBeAHeader()
    {
        using var baseFactory = new PayloadApiFactory();
        using var factory = Factory(baseFactory, new FakeCheckout(new InvalidOperationException("must not be called")));
        using var client = Client(factory);

        using var response = await client.PostAsJsonAsync(Route, new
        {
            spiToken = "spi-token-1",
            srcDpaId = "823ef281-1a2e-4204-a69c-43d355522a35",
            srcCorrelationId = "corr-1",
            merchantTransactionId = "mtid-1",
            flowId = "flow-1",
            transactionAmount = 31.25m,
            transactionCurrencyCode = "USD",
            xCorrelationId = "abc\r\nSet-Cookie: x=1"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnyMerchantDpaIsPassedToTheCheckout()
    {
        var checkout = new FakeCheckout(new CompleteCheckoutResult
        {
            Payload = JsonSerializer.Deserialize<DecryptedPayloadDto>("""{"card":{"primaryAccountNumber":"5120350100064537","panExpirationMonth":"12","panExpirationYear":"2030"},"dynamicData":{"dynamicDataType":"NONE"}}""")!
        });
        using var baseFactory = new PayloadApiFactory();
        using var factory = Factory(baseFactory, checkout);
        using var client = Client(factory);

        using var response = await client.PostAsJsonAsync(Route, new
        {
            spiToken = "spi-token-1",
            srcDpaId = "another-merchant-dpa",
            srcCorrelationId = "corr-1",
            merchantTransactionId = "mtid-1",
            flowId = "flow-1",
            transactionAmount = 31.25m,
            transactionCurrencyCode = "USD",
            xCorrelationId = "x-corr-1"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("another-merchant-dpa", checkout.LastRequest!.SrcDpaId);
    }

    [Fact]
    public async Task SuccessfulCheckoutIsWrittenToTheDailyTransactionFile()
    {
        var checkout = new FakeCheckout(new CompleteCheckoutResult
        {
            Payload = JsonSerializer.Deserialize<DecryptedPayloadDto>("""{"card":{"primaryAccountNumber":"5120350100064537","panExpirationMonth":"12","panExpirationYear":"2030"},"dynamicData":{"dynamicDataType":"NONE"}}""")!
        });
        using var baseFactory = new PayloadApiFactory();
        using var factory = Factory(baseFactory, checkout);
        using var client = Client(factory);

        using var response = await client.PostAsJsonAsync(Route, FrontRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lines = await ReadTransactionFileAsync(baseFactory.TransactionDirectory, expectedLines: 2);
        Assert.Equal(CsvCheckoutTransactionFile.Header, lines[0]);
        var columns = lines[1].Split(',');
        Assert.Matches("^\\d{8}$", columns[0]);
        Assert.Matches("^\\d{6}$", columns[1]);
        Assert.Equal(
            ["spi-token-1", "31.25", "USD", "823ef281-1a2e-4204-a69c-43d355522a35",
             "34f4a04b.2ce61515-55b7-4c09-845b-45492c2ef327", "0a4e0d3.34f4a04b.f91587770b3c99ce1090fe8a4f2a931b10d7acfc",
             "34f4a04b.2ce61515-55b7-4c09-845b-45492c2ef327.1790888627", "7d1f6c2e-9a43-4b8e-b1a2-3f5c8e0d4a17"],
            columns[2..]);
        // Never card data.
        Assert.DoesNotContain("5120350100064537", string.Join('\n', lines), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedCheckoutIsNotWrittenToTheTransactionFile()
    {
        using var baseFactory = new PayloadApiFactory();
        using var factory = Factory(baseFactory, new FakeCheckout(new PayloadDecryptionException(PayloadDecryptionError.DecryptionFailed)));
        using var client = Client(factory);

        using var response = await client.PostAsJsonAsync(Route, FrontRequest);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await Task.Delay(200);
        Assert.False(Directory.Exists(baseFactory.TransactionDirectory)
            && Directory.EnumerateFiles(baseFactory.TransactionDirectory).Any());
    }

    /// <summary>The row is written after the response is sent, so wait briefly for it.</summary>
    private static async Task<string[]> ReadTransactionFileAsync(string directory, int expectedLines)
    {
        for (var attempt = 0; ; attempt++)
        {
            var file = Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "ctp_txn_*.csv").SingleOrDefault() : null;
            if (file is not null)
            {
                // Shared like the batch reader must open it: the API may still be appending.
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var lines = (await reader.ReadToEndAsync()).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length >= expectedLines || attempt == 50)
                {
                    Assert.Matches("^ctp_txn_\\d{8}\\.csv$", Path.GetFileName(file));
                    return lines;
                }
            }
            else if (attempt == 50)
            {
                Assert.Fail("The transaction file was not written.");
            }

            await Task.Delay(100);
        }
    }

    private static async Task<JsonElement> PostAsync(string decryptedPayloadJson)
    {
        var result = new CompleteCheckoutResult
        {
            MerchantTransactionId = "0a4e0d3.34f4a04b.f91587770b3c99ce1090fe8a4f2a931b10d7acfc",
            CorrelationId = "34f4a04b.2ce61515-55b7-4c09-845b-45492c2ef327",
            Eci = "06",
            Payload = JsonSerializer.Deserialize<DecryptedPayloadDto>(decryptedPayloadJson)!
        };
        using var baseFactory = new PayloadApiFactory();
        using var factory = Factory(baseFactory, new FakeCheckout(result));
        using var client = Client(factory);

        using var response = await client.PostAsJsonAsync(Route, FrontRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("7d1f6c2e-9a43-4b8e-b1a2-3f5c8e0d4a17", response.Headers.GetValues("X-Correlation-Id").Single());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static WebApplicationFactory<Program> Factory(PayloadApiFactory baseFactory, FakeCheckout checkout) =>
        baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(
            services => services.AddScoped<IMastercardCheckoutService>(_ => checkout)));

    private static HttpClient Client(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("X-Api-Key", PayloadApiFactory.ApiKey);
        return client;
    }

    private sealed class FakeCheckout : IMastercardCheckoutService
    {
        private readonly CompleteCheckoutResult? result;
        private readonly Exception? error;

        public FakeCheckout(CompleteCheckoutResult result) => this.result = result;

        public FakeCheckout(Exception error) => this.error = error;

        public CompleteCheckoutRequest? LastRequest { get; private set; }

        public Task<CompleteCheckoutResult> CompleteAsync(CompleteCheckoutRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return error is null ? Task.FromResult(result!) : Task.FromException<CompleteCheckoutResult>(error);
        }

        public Task ConfirmAsync(CheckoutConfirmationRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
