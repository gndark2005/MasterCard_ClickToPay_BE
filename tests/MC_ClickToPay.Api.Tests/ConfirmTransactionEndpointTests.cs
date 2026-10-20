using System.Net;
using System.Net.Http.Json;
using MC_ClickToPay.Services.Abstractions;
using MC_ClickToPay.Services.Checkout;
using MC_ClickToPay.Services.Exceptions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MC_ClickToPay.Api.Tests;

public sealed class ConfirmTransactionEndpointTests
{
    private const string ApprovedRow =
        "20261015,090507,spi-1,31.25,USD,dpa-1,corr-1,mtid-1,flow-1,x-1,01,01,01,The payment was successful,123456,mastercard,,,,";

    [Fact]
    public async Task ConfirmsTheRowOfTheSpiTokenAndUpdatesIt()
    {
        var checkout = new FakeCheckout(new CheckoutConfirmationResult { FlowId = "flow-from-mastercard" });
        using var baseFactory = new PayloadApiFactory();
        await WriteFileAsync(baseFactory, ApprovedRow);
        using var factory = Factory(baseFactory, checkout);
        using var client = Client(factory);

        using var response = await client.PostAsJsonAsync(Route("spi-1"), ApprovedBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("flow-from-mastercard", response.Headers.GetValues("X-Src-Cx-Flow-Id").Single());
        Assert.Equal("x-1", response.Headers.GetValues("X-Correlation-Id").Single());
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("Confirmed", body.GetProperty("status").GetString());
        Assert.Equal("ctp_txn_20261015.csv", body.GetProperty("fileName").GetString());
        Assert.Equal(2, body.GetProperty("line").GetInt32());
        Assert.Equal("corr-1", body.GetProperty("correlationId").GetString());
        // The SPI token is never sent back.
        Assert.DoesNotContain("spi-1", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var sent = checkout.LastRequest!;
        Assert.Equal("corr-1", sent.CorrelationId);
        Assert.Equal("mtid-1", sent.MerchantTransactionId);
        Assert.Equal("flow-1", sent.FlowId);
        Assert.Equal("123456", sent.ConfirmationData!.NetworkAuthorizationCode);
        Assert.Equal(31.25m, sent.ConfirmationData.TransactionAmount!.Amount);

        var row = Assert.Single(await ReadFileAsync(baseFactory));
        Assert.Equal("Confirmed", row.ConfirmationResult);
    }

    [Fact]
    public async Task ASecondConfirmationReturns409()
    {
        var checkout = new FakeCheckout(new CheckoutConfirmationResult());
        using var baseFactory = new PayloadApiFactory();
        await WriteFileAsync(baseFactory, ApprovedRow);
        using var factory = Factory(baseFactory, checkout);
        using var client = Client(factory);

        using var first = await client.PostAsJsonAsync(Route("spi-1"), ApprovedBody);
        using var second = await client.PostAsJsonAsync(Route("spi-1"), ApprovedBody);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task AnUnknownSpiTokenReturns404WithoutCallingMastercard()
    {
        var checkout = new FakeCheckout(new CheckoutConfirmationResult());
        using var baseFactory = new PayloadApiFactory();
        await WriteFileAsync(baseFactory, ApprovedRow);
        using var factory = Factory(baseFactory, checkout);
        using var client = Client(factory);

        using var response = await client.PostAsJsonAsync(Route("spi-unknown"), ApprovedBody);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Transaction not found.", await response.Content.ReadAsStringAsync());
        Assert.Null(checkout.LastRequest);
    }

    [Fact]
    public async Task ARowStraightFromTheCheckoutGetsTheOutcomeReceived()
    {
        var checkout = new FakeCheckout(new CheckoutConfirmationResult());
        using var baseFactory = new PayloadApiFactory();
        await WriteFileAsync(baseFactory, "20261015,090507,spi-1,31.25,USD,dpa-1,corr-1,mtid-1,flow-1,x-1,,,,,,,,,,");
        using var factory = Factory(baseFactory, checkout);
        using var client = Client(factory);

        using var response = await client.PostAsJsonAsync(Route("spi-1"),
            new { approved = false, responseMessage = "Declined" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("02", checkout.LastRequest!.ConfirmationData!.ConfirmationStatus);
        var row = Assert.Single(await ReadFileAsync(baseFactory));
        Assert.Equal(("01", "02", "02", "Declined"),
            (row.CheckoutEventType, row.CheckoutEventStatus, row.ConfirmationStatus, row.ConfirmationReason));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"approved":true,"authorizationCode":"12345678901234567890123456789012345678901234567890123456789012345"}""")]
    public async Task AnInvalidBodyReturns400(string body)
    {
        var checkout = new FakeCheckout(new CheckoutConfirmationResult());
        using var baseFactory = new PayloadApiFactory();
        await WriteFileAsync(baseFactory, ApprovedRow);
        using var factory = Factory(baseFactory, checkout);
        using var client = Client(factory);

        using var response = await client.PostAsync(Route("spi-1"),
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(checkout.LastRequest);
    }

    [Fact]
    public async Task ATooLongSpiTokenReturns400()
    {
        var checkout = new FakeCheckout(new CheckoutConfirmationResult());
        using var baseFactory = new PayloadApiFactory();
        using var factory = Factory(baseFactory, checkout);
        using var client = Client(factory);

        using var response = await client.PostAsJsonAsync(Route(new string('a', 2049)), ApprovedBody);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(checkout.LastRequest);
    }

    [Fact]
    public async Task MissingApiKeyReturns401()
    {
        using var baseFactory = new PayloadApiFactory();
        using var factory = Factory(baseFactory, new FakeCheckout(new CheckoutConfirmationResult()));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        using var response = await client.PostAsJsonAsync(Route("spi-1"), ApprovedBody);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(400, HttpStatusCode.BadRequest)]
    [InlineData(404, HttpStatusCode.NotFound)]
    [InlineData(401, HttpStatusCode.BadGateway)]
    [InlineData(500, HttpStatusCode.BadGateway)]
    public async Task MastercardErrorsAreMappedAndRecordedInTheRow(int mastercardStatus, HttpStatusCode expected)
    {
        var error = new MastercardCheckoutException(
            $"Mastercard /srci/api/checkout/confirmations failed with HTTP {mastercardStatus}. [REASON] Description", mastercardStatus);
        using var baseFactory = new PayloadApiFactory();
        await WriteFileAsync(baseFactory, ApprovedRow);
        using var factory = Factory(baseFactory, new FakeCheckout(error));
        using var client = Client(factory);

        using var response = await client.PostAsJsonAsync(Route("spi-1"), ApprovedBody);

        Assert.Equal(expected, response.StatusCode);
        Assert.Contains("[REASON] Description", await response.Content.ReadAsStringAsync());
        Assert.Equal("x-1", response.Headers.GetValues("X-Correlation-Id").Single());
        Assert.Equal("ConfirmationFailed", Assert.Single(await ReadFileAsync(baseFactory)).ConfirmationResult);
    }

    // A PowerTranz /spi/Payment result.
    private static readonly object ApprovedBody = new
    {
        approved = true,
        authorizationCode = "123456",
        responseMessage = "The payment was successful",
        networkTransactionIdentifier = "mastercard"
    };

    private static string Route(string spiToken) => $"/api/confirmations/{Uri.EscapeDataString(spiToken)}";

    private static async Task WriteFileAsync(PayloadApiFactory factory, params string[] rows)
    {
        Directory.CreateDirectory(factory.TransactionDirectory);
        await File.WriteAllTextAsync(Path.Combine(factory.TransactionDirectory, "ctp_txn_20261015.csv"),
            string.Join('\n', [CsvCheckoutTransactionFile.Header, .. rows]) + "\n");
    }

    private static async Task<IReadOnlyList<CheckoutTransactionRow>> ReadFileAsync(PayloadApiFactory factory) =>
        CheckoutTransactionFileReader.Read(await File.ReadAllTextAsync(Path.Combine(factory.TransactionDirectory, "ctp_txn_20261015.csv")));

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
        private readonly Func<CheckoutConfirmationRequest, CheckoutConfirmationResult> confirm;

        public FakeCheckout(CheckoutConfirmationResult result) => confirm = _ => result;

        public FakeCheckout(Exception error) => confirm = _ => throw error;

        public CheckoutConfirmationRequest? LastRequest { get; private set; }

        public Task<CompleteCheckoutResult> CompleteAsync(CompleteCheckoutRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CheckoutConfirmationResult> ConfirmAsync(CheckoutConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            try
            {
                return Task.FromResult(confirm(request));
            }
            catch (Exception ex)
            {
                return Task.FromException<CheckoutConfirmationResult>(ex);
            }
        }
    }
}
