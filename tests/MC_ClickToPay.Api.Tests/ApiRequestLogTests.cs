using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using MC_ClickToPay.Services.Abstractions;
using MC_ClickToPay.Services.Checkout;
using MC_ClickToPay.Services.Database;
using MC_ClickToPay.Services.Exceptions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MC_ClickToPay.Api.Tests;

public sealed class ApiRequestLogTests
{
    private const string SpiToken = "spi-secret-token-1";

    [Fact]
    public async Task AConfirmationIsLoggedWithTheRouteTemplateNeverTheSpiToken()
    {
        var store = new RecordingStore();
        using var baseFactory = new PayloadApiFactory();
        await WriteFileAsync(baseFactory,
            $"20261015,090507,{SpiToken},31.25,USD,dpa-1,corr-1,mtid-1,flow-1,x-1,,,,,,,,,,");
        using var factory = Factory(baseFactory, store, new FakeCheckout(_ => new CheckoutConfirmationResult()));
        using var client = Client(factory);

        using var response = await client.PostAsJsonAsync($"/api/confirmations/{SpiToken}",
            new { approved = true, authorizationCode = "123456" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entry = await store.SingleAsync();
        Assert.Equal("POST", entry.Method);
        Assert.Equal("/api/confirmations/{spiToken}", entry.Route);
        Assert.Equal(200, entry.HttpStatus);
        Assert.Equal("corr-1", entry.SrcCorrelationId);
        Assert.Equal("x-1", entry.XCorrelationId);
        Assert.Null(entry.ErrorTitle);
        Assert.Null(entry.ErrorDetail);
        Assert.False(string.IsNullOrEmpty(entry.TraceId));
        Assert.DoesNotContain(SpiToken, entry.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidationErrorsAreLoggedWithTheirMessages()
    {
        var store = new RecordingStore();
        using var baseFactory = new PayloadApiFactory();
        using var factory = Factory(baseFactory, store, new FakeCheckout(_ => new CheckoutConfirmationResult()));
        using var client = Client(factory);

        using var response = await client.PostAsJsonAsync($"/api/confirmations/{SpiToken}", new { approved = true });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var entry = await store.SingleAsync();
        Assert.Equal(400, entry.HttpStatus);
        Assert.Equal("One or more errors occurred!", entry.ErrorTitle);
        Assert.Contains("authorizationCode is required when approved is true", entry.ErrorDetail);
        Assert.DoesNotContain(SpiToken, entry.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MastercardErrorsAreLoggedWithTheirDetail()
    {
        var store = new RecordingStore();
        using var baseFactory = new PayloadApiFactory();
        var checkout = new FakeCheckout(_ => throw new MastercardCheckoutException(
            "Mastercard /srci/api/checkout/confirmations failed with HTTP 400. [INVALID_ARGUMENT] One field is wrong", 400));
        await WriteFileAsync(baseFactory,
            $"20261015,090507,{SpiToken},31.25,USD,dpa-1,corr-1,mtid-1,flow-1,x-1,,,,,,,,,,");
        using var factory = Factory(baseFactory, store, checkout);
        using var client = Client(factory);

        using var response = await client.PostAsJsonAsync($"/api/confirmations/{SpiToken}",
            new { approved = true, authorizationCode = "123456" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var entry = await store.SingleAsync();
        Assert.Equal(400, entry.HttpStatus);
        Assert.Equal("Mastercard rejected the confirmation.", entry.ErrorTitle);
        Assert.Contains("[INVALID_ARGUMENT]", entry.ErrorDetail);
        Assert.Equal("corr-1", entry.SrcCorrelationId);
    }

    [Fact]
    public async Task UnhandledExceptionsAreLoggedAs500WithTheirType()
    {
        var store = new RecordingStore();
        using var baseFactory = new PayloadApiFactory();
        await WriteFileAsync(baseFactory,
            $"20261015,090507,{SpiToken},31.25,USD,dpa-1,corr-1,mtid-1,flow-1,x-1,,,,,,,,,,");
        using var factory = Factory(baseFactory, store,
            new FakeCheckout(_ => throw new NotSupportedException("unexpected")));
        using var client = Client(factory);

        using var response = await client.PostAsJsonAsync($"/api/confirmations/{SpiToken}",
            new { approved = true, authorizationCode = "123456" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var entry = await store.SingleAsync();
        Assert.Equal(500, entry.HttpStatus);
        Assert.Equal(typeof(NotSupportedException).FullName, entry.ExceptionType);
    }

    [Fact]
    public async Task RequestsWithoutApiKeyAreLogged()
    {
        var store = new RecordingStore();
        using var baseFactory = new PayloadApiFactory();
        using var factory = Factory(baseFactory, store, new FakeCheckout(_ => new CheckoutConfirmationResult()));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        using var response = await client.PostAsJsonAsync("/api/checkout", new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var entry = await store.SingleAsync();
        Assert.Equal(("/api/checkout", 401), (entry.Route, entry.HttpStatus));
    }

    [Fact]
    public async Task SwaggerIsNotLogged()
    {
        var store = new RecordingStore();
        using var baseFactory = new PayloadApiFactory();
        using var factory = Factory(baseFactory, store, new FakeCheckout(_ => new CheckoutConfirmationResult()));
        using var client = Client(factory);

        using var response = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Task.Delay(300);
        Assert.Empty(store.Entries);
    }

    private static async Task WriteFileAsync(PayloadApiFactory factory, string row)
    {
        Directory.CreateDirectory(factory.TransactionDirectory);
        await File.WriteAllTextAsync(Path.Combine(factory.TransactionDirectory, "ctp_txn_20261015.csv"),
            $"{CsvCheckoutTransactionFile.Header}\n{row}\n");
    }

    private static WebApplicationFactory<Program> Factory(PayloadApiFactory baseFactory, RecordingStore store, FakeCheckout checkout) =>
        baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IApiRequestLogStore>(store);
            services.AddScoped<IMastercardCheckoutService>(_ => checkout);
        }));

    private static HttpClient Client(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("X-Api-Key", PayloadApiFactory.ApiKey);
        return client;
    }

    private sealed class RecordingStore : IApiRequestLogStore
    {
        public ConcurrentQueue<ApiRequestLogEntry> Entries { get; } = new();

        public Task WriteAsync(IReadOnlyList<ApiRequestLogEntry> entries, CancellationToken cancellationToken = default)
        {
            foreach (var entry in entries)
            {
                Entries.Enqueue(entry);
            }

            return Task.CompletedTask;
        }

        /// <summary>The row is written in the background after the response: waits for it.</summary>
        public async Task<ApiRequestLogEntry> SingleAsync()
        {
            for (var attempt = 0; attempt < 100 && Entries.IsEmpty; attempt++)
            {
                await Task.Delay(50);
            }

            return Assert.Single(Entries);
        }
    }

    private sealed class FakeCheckout(Func<CheckoutConfirmationRequest, CheckoutConfirmationResult> confirm) : IMastercardCheckoutService
    {
        public Task<CompleteCheckoutResult> CompleteAsync(CompleteCheckoutRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CheckoutConfirmationResult> ConfirmAsync(CheckoutConfirmationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(confirm(request));
    }
}
