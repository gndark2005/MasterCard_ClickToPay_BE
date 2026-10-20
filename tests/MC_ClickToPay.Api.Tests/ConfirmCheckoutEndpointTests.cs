using System.Net;
using System.Net.Http.Json;
using System.Text;
using MC_ClickToPay.Services.Abstractions;
using MC_ClickToPay.Services.Checkout;
using MC_ClickToPay.Services.Exceptions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MC_ClickToPay.Api.Tests;

public sealed class ConfirmCheckoutEndpointTests
{
    private const string Route = "/api/checkout/confirmations";

    // Mastercard's request sample (amount as a string) plus flowId and xCorrelationId.
    private const string SampleBody = """
        {
          "merchantTransactionId": "0a4e0d3.34f4a04b.bddaa2cf22b394705447f2b03ca662eb86d0b253",
          "correlationId": "43465920-50e4-475f-a8d8-198a842fd3bf",
          "flowId": "34f4a04b.2ce61515-55b7-4c09-845b-45492c2ef327.1790888627",
          "xCorrelationId": "7d1f6c2e-9a43-4b8e-b1a2-3f5c8e0d4a17",
          "confirmationData": {
            "checkoutEventType": "01",
            "checkoutEventStatus": "01",
            "confirmationStatus": "01",
            "confirmationReason": "The payment was successful",
            "confirmationTimestamp": "2020-04-25T01:15:00.000Z",
            "networkAuthorizationCode": "2020404",
            "networkTransactionIdentifier": "mastercard",
            "transactionAmount": { "transactionAmount": "1200", "transactionCurrencyCode": "USD" }
          }
        }
        """;

    [Fact]
    public async Task ForwardsTheConfirmationAndReturns204WithTheFlowId()
    {
        var checkout = new FakeCheckout(new CheckoutConfirmationResult { FlowId = "flow-from-mastercard" });
        using var baseFactory = new PayloadApiFactory();
        using var factory = Factory(baseFactory, checkout);
        using var client = Client(factory);

        using var response = await client.PostAsync(Route, Json(SampleBody));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("flow-from-mastercard", response.Headers.GetValues("X-Src-Cx-Flow-Id").Single());
        Assert.Equal("7d1f6c2e-9a43-4b8e-b1a2-3f5c8e0d4a17", response.Headers.GetValues("X-Correlation-Id").Single());

        var sent = checkout.LastRequest!;
        Assert.Equal("43465920-50e4-475f-a8d8-198a842fd3bf", sent.CorrelationId);
        Assert.Equal("0a4e0d3.34f4a04b.bddaa2cf22b394705447f2b03ca662eb86d0b253", sent.MerchantTransactionId);
        Assert.Equal("34f4a04b.2ce61515-55b7-4c09-845b-45492c2ef327.1790888627", sent.FlowId);
        Assert.Equal("01", sent.ConfirmationData!.ConfirmationStatus);
        Assert.Equal("2020404", sent.ConfirmationData.NetworkAuthorizationCode);
        Assert.Equal(1200m, sent.ConfirmationData.TransactionAmount!.Amount);
        Assert.Equal("USD", sent.ConfirmationData.TransactionAmount.CurrencyCode);
        Assert.Equal(new DateTimeOffset(2020, 4, 25, 1, 15, 0, TimeSpan.Zero), sent.ConfirmationData.ConfirmationTimestamp);
    }

    [Theory]
    [InlineData("""{"xCorrelationId":"x-1","confirmationData":{"checkoutEventType":"01","checkoutEventStatus":"01","confirmationStatus":"01"}}""")]
    [InlineData("""{"correlationId":"c-1","xCorrelationId":"x-1"}""")]
    [InlineData("""{"correlationId":"c-1","confirmationData":{"checkoutEventType":"01","checkoutEventStatus":"01","confirmationStatus":"01"}}""")]
    [InlineData("""{"correlationId":"c-1","xCorrelationId":"x-1","confirmationData":{"checkoutEventType":"1","checkoutEventStatus":"01","confirmationStatus":"01"}}""")]
    [InlineData("""{"correlationId":"c-1","xCorrelationId":"x-1","confirmationData":{"checkoutEventType":"01","checkoutEventStatus":"01","confirmationStatus":"OK"}}""")]
    [InlineData("""{"correlationId":"c-1","xCorrelationId":"x-1","confirmationData":{"checkoutEventType":"01","checkoutEventStatus":"01","confirmationStatus":"01","transactionAmount":{"transactionAmount":"0","transactionCurrencyCode":"USD"}}}""")]
    [InlineData("""{"correlationId":"c-1","xCorrelationId":"x-1","confirmationData":{"checkoutEventType":"01","checkoutEventStatus":"01","confirmationStatus":"01","transactionAmount":{"transactionAmount":"10","transactionCurrencyCode":"840"}}}""")]
    public async Task InvalidRequestsReturn400WithoutCallingMastercard(string body)
    {
        var checkout = new FakeCheckout(new CheckoutConfirmationResult());
        using var baseFactory = new PayloadApiFactory();
        using var factory = Factory(baseFactory, checkout);
        using var client = Client(factory);

        using var response = await client.PostAsync(Route, Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(checkout.LastRequest);
    }

    [Fact]
    public async Task MissingApiKeyReturns401()
    {
        using var baseFactory = new PayloadApiFactory();
        using var factory = Factory(baseFactory, new FakeCheckout(new CheckoutConfirmationResult()));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        using var response = await client.PostAsync(Route, Json(SampleBody));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(400, HttpStatusCode.BadRequest)]
    [InlineData(404, HttpStatusCode.NotFound)]
    [InlineData(401, HttpStatusCode.BadGateway)]
    [InlineData(403, HttpStatusCode.BadGateway)]
    [InlineData(500, HttpStatusCode.BadGateway)]
    [InlineData(503, HttpStatusCode.BadGateway)]
    public async Task MastercardErrorsAreMapped(int mastercardStatus, HttpStatusCode expected)
    {
        var error = new MastercardCheckoutException(
            $"Mastercard /srci/api/checkout/confirmations failed with HTTP {mastercardStatus}. [REASON] Description", mastercardStatus);
        using var baseFactory = new PayloadApiFactory();
        using var factory = Factory(baseFactory, new FakeCheckout(error));
        using var client = Client(factory);

        using var response = await client.PostAsync(Route, Json(SampleBody));

        Assert.Equal(expected, response.StatusCode);
        Assert.Contains("[REASON] Description", await response.Content.ReadAsStringAsync());
        Assert.Equal("7d1f6c2e-9a43-4b8e-b1a2-3f5c8e0d4a17", response.Headers.GetValues("X-Correlation-Id").Single());
    }

    [Fact]
    public async Task UnreachableMastercardReturns502()
    {
        var error = new MastercardCheckoutException("Could not reach Mastercard at sandbox.api.mastercard.com.");
        using var baseFactory = new PayloadApiFactory();
        using var factory = Factory(baseFactory, new FakeCheckout(error));
        using var client = Client(factory);

        using var response = await client.PostAsync(Route, Json(SampleBody));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    private const string FileHeader =
        "date,time,spitoken,transaction_amount,transaction_currency,SrcDpaId,SrcCorrelationId,MerchantTransactionId,FlowId,XCorrelationId," +
        "checkout_event_type,checkout_event_status,confirmation_status,confirmation_reason,network_authorization_code,network_transaction_identifier";

    [Fact]
    public async Task UploadedFileIsConfirmedRowByRow()
    {
        var checkout = new FakeCheckout(request => request.CorrelationId == "corr-missing"
            ? throw new MastercardCheckoutException("[NOT_FOUND] Transaction data not found", 404)
            : new CheckoutConfirmationResult { FlowId = "flow-back" });
        using var baseFactory = new PayloadApiFactory();
        using var factory = Factory(baseFactory, checkout);
        using var client = Client(factory);

        var csv = $"""
            {FileHeader}
            20261015,090507,spi-1,31.25,USD,dpa-1,corr-ok,mtid-1,flow-1,x-1,01,01,01,The payment was successful,123456,mastercard
            20261015,090508,spi-2,10.00,USD,dpa-1,corr-missing,mtid-2,flow-2,x-2,01,01,01,,,
            20261015,090509,spi-3,5.00,USD,dpa-1,corr-pending,mtid-3,flow-3,x-3,,,,,,
            """;
        using var response = await client.PostAsync(Route, File(csv, "ctp_txn_20261015.csv"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("ctp_txn_20261015.csv", body.GetProperty("fileName").GetString());
        Assert.Equal(3, body.GetProperty("totalRows").GetInt32());
        Assert.Equal(1, body.GetProperty("confirmed").GetInt32());
        Assert.Equal(1, body.GetProperty("failed").GetInt32());
        Assert.Equal(1, body.GetProperty("skipped").GetInt32());
        var results = body.GetProperty("results");
        Assert.Equal("Confirmed", results[0].GetProperty("status").GetString());
        Assert.Equal("flow-back", results[0].GetProperty("flowId").GetString());
        Assert.Equal("Failed", results[1].GetProperty("status").GetString());
        Assert.Equal(404, results[1].GetProperty("httpStatus").GetInt32());
        Assert.Equal("Skipped", results[2].GetProperty("status").GetString());
        Assert.Equal(4, results[2].GetProperty("line").GetInt32());

        // The SPI tokens in the file are never sent back.
        Assert.DoesNotContain("spi-1", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileWrittenByTheCheckoutIsAccepted()
    {
        // Same header the API writes: rows straight from /api/checkout have no outcome yet and are skipped.
        var checkout = new FakeCheckout(new CheckoutConfirmationResult());
        using var baseFactory = new PayloadApiFactory();
        using var factory = Factory(baseFactory, checkout);
        using var client = Client(factory);

        var csv = $"{CsvCheckoutTransactionFile.Header}\n20261015,090507,spi-1,31.25,USD,dpa-1,corr-1,mtid-1,flow-1,x-1,,,,,,\n";
        using var response = await client.PostAsync(Route, File(csv, "ctp_txn_20261015.csv"));

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(1, body.GetProperty("skipped").GetInt32());
        Assert.Null(checkout.LastRequest);
    }

    [Theory]
    [InlineData("date,time\n20261015,090507", "ctp_txn_20261015.csv")]
    [InlineData("", "ctp_txn_20261015.csv")]
    [InlineData("anything", "ctp_txn_20261015.txt")]
    public async Task InvalidFilesReturn400(string content, string fileName)
    {
        var checkout = new FakeCheckout(new CheckoutConfirmationResult());
        using var baseFactory = new PayloadApiFactory();
        using var factory = Factory(baseFactory, checkout);
        using var client = Client(factory);

        using var response = await client.PostAsync(Route, File(content, fileName));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(checkout.LastRequest);
    }

    [Fact]
    public async Task FilesAboveTheDefaultBodyLimitAreAccepted()
    {
        var checkout = new FakeCheckout(new CheckoutConfirmationResult());
        using var baseFactory = new PayloadApiFactory();
        using var factory = Factory(baseFactory, checkout);
        using var client = Client(factory);

        // About 600 KiB of rows without an outcome: above the API's 256 KiB default.
        var row = "20261015,090507,spi-token,31.25,USD,dpa-1,corr-1,mtid-1,flow-1,x-1,,,,,,";
        var csv = FileHeader + "\n" + string.Join("\n", Enumerable.Repeat(row, 8_000));
        using var response = await client.PostAsync(Route, File(csv, "ctp_txn_20261015.csv"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(8_000, body.GetProperty("skipped").GetInt32());
    }

    private static MultipartFormDataContent File(string content, string fileName)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", fileName);
        return form;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

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

        public FakeCheckout(Func<CheckoutConfirmationRequest, CheckoutConfirmationResult> confirm) => this.confirm = confirm;

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
