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
        xCorrelationId = "7d1f6c2e-9a43-4b8e-b1a2-3f5c8e0d4a17"
    };

    [Fact]
    public async Task TokenPayloadIsReturnedWithCardFilledFromTheToken()
    {
        var decrypted = """
            {"token":{"paymentToken":"5186051929138756","tokenExpirationMonth":"09","tokenExpirationYear":"2029","cardholderFullName":"Adelle Ryan"},
             "dynamicData":{"dynamicDataValue":"AAnIdMIzlaW+ACJyvravAAADFA==","dynamicDataType":"CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM"},
             "billingAddress":{"city":"Maineville","countryCode":"US"}}
            """;

        var body = await PostAsync(decrypted);

        Assert.Equal("NetworkToken", body.GetProperty("credentialType").GetString());
        Assert.Equal("CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM", body.GetProperty("dynamicDataType").GetString());
        Assert.Equal("06", body.GetProperty("eci").GetString());
        var payload = body.GetProperty("payload");
        Assert.Equal("5186051929138756", payload.GetProperty("card").GetProperty("primaryAccountNumber").GetString());
        Assert.Equal("09", payload.GetProperty("card").GetProperty("panExpirationMonth").GetString());
        Assert.Equal("5186051929138756", payload.GetProperty("token").GetProperty("paymentToken").GetString());
        Assert.Equal("AAnIdMIzlaW+ACJyvravAAADFA==", payload.GetProperty("dynamicData").GetProperty("dynamicDataValue").GetString());
        Assert.Equal("Maineville", payload.GetProperty("billingAddress").GetProperty("city").GetString());
    }

    [Theory]
    [InlineData("""{"card":{"primaryAccountNumber":"5120350100064537","panExpirationMonth":"07","panExpirationYear":"2029"},"token":{"paymentToken":"************9541"},"dynamicData":{"dynamicDataValue":"AH14E2rQmy6mABQkMkPpAAADFA==","dynamicDataType":"CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM"}}""", "CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM")]
    [InlineData("""{"card":{"primaryAccountNumber":"5120350100064537","panExpirationMonth":"12","panExpirationYear":"2030"},"dynamicData":{"dynamicDataValue":"637","dynamicDataType":"DYNAMIC_CARD_SECURITY_CODE"}}""", "DYNAMIC_CARD_SECURITY_CODE")]
    [InlineData("""{"card":{"primaryAccountNumber":"5120350100064537","panExpirationMonth":"12","panExpirationYear":"2030"},"dynamicData":{"dynamicDataType":"NONE"}}""", "NONE")]
    public async Task CardPayloadsAreReturnedWithThePan(string decrypted, string dynamicDataType)
    {
        var body = await PostAsync(decrypted);

        Assert.Equal("Pan", body.GetProperty("credentialType").GetString());
        Assert.Equal(dynamicDataType, body.GetProperty("dynamicDataType").GetString());
        Assert.Equal("5120350100064537", body.GetProperty("payload").GetProperty("card").GetProperty("primaryAccountNumber").GetString());
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
            xCorrelationId = "abc\r\nSet-Cookie: x=1"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RejectsAnotherDpa()
    {
        using var baseFactory = new PayloadApiFactory();
        using var factory = Factory(baseFactory, new FakeCheckout(new InvalidOperationException("must not be called")));
        using var client = Client(factory);

        using var response = await client.PostAsJsonAsync(Route, new
        {
            spiToken = "spi-token-1",
            srcDpaId = "00000000-0000-0000-0000-000000000000",
            srcCorrelationId = "corr-1",
            merchantTransactionId = "mtid-1",
            flowId = "flow-1",
            xCorrelationId = "x-corr-1"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("srcDpaId", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
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

        public Task<CompleteCheckoutResult> CompleteAsync(CompleteCheckoutRequest request, CancellationToken cancellationToken = default) =>
            error is null ? Task.FromResult(result!) : Task.FromException<CompleteCheckoutResult>(error);

        public Task ConfirmAsync(CheckoutConfirmationRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
