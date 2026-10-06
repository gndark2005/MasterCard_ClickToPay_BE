using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MC_ClickToPay.Services;
using MC_ClickToPay.Services.Abstractions;
using MC_ClickToPay.Services.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MC_ClickToPay.PaymentDemo.Api.Tests;

public sealed class SamplePayloadEndpointTests
{
    private const string Route = "/api/demo/payloads/sample";

    [Fact]
    public async Task EncryptsConfiguredTestPaymentAsDecryptableJwe()
    {
        using var factory = new PaymentDemoApiFactory();
        using var client = factory.CreateApiClient();

        var encrypted = await PaymentDemoApiFactory.CreateSamplePayloadAsync(client);

        Assert.Equal(5, encrypted.Split('.').Length);
        Assert.DoesNotContain(PaymentDemoApiFactory.ConfiguredToken, encrypted);
        var payload = await DecryptAsync(factory, encrypted);
        Assert.Equal(PaymentDemoApiFactory.ConfiguredToken, payload.Token!.PaymentToken);
        Assert.Equal(PaymentDemoApiFactory.ConfiguredCryptogram, payload.DynamicData!.DynamicDataValue);
        Assert.Equal("US", payload.BillingAddress!.CountryCode);
    }

    [Fact]
    public async Task BodyFieldsOverrideConfiguredTestPayment()
    {
        using var factory = new PaymentDemoApiFactory();
        using var client = factory.CreateApiClient();

        var encrypted = await PaymentDemoApiFactory.CreateSamplePayloadAsync(client,
            new { paymentToken = "5100000000000008", tokenExpirationYear = "2031" });

        var payload = await DecryptAsync(factory, encrypted);
        Assert.Equal("5100000000000008", payload.Token!.PaymentToken);
        Assert.Equal("2031", payload.Token.TokenExpirationYear);
        Assert.Equal("12", payload.Token.TokenExpirationMonth);
    }

    [Fact]
    public async Task ConfiguredTestCardCanBeChangedThroughConfiguration()
    {
        using var factory = new PaymentDemoApiFactory();
        factory.Settings["PaymentDemo:TestPayment:PaymentToken"] = "5100000000000008";
        using var client = factory.CreateApiClient();

        var payload = await DecryptAsync(factory, await PaymentDemoApiFactory.CreateSamplePayloadAsync(client));

        Assert.Equal("5100000000000008", payload.Token!.PaymentToken);
    }

    [Fact]
    public async Task DisabledEndpointReturnsNotFound()
    {
        using var factory = new PaymentDemoApiFactory();
        factory.Settings["PaymentDemo:EnableSamplePayloadEndpoint"] = "false";
        using var client = factory.CreateApiClient();

        using var response = await client.PostAsJsonAsync(Route, new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("sample_payloads_disabled", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task RequiresApiKey()
    {
        using var factory = new PaymentDemoApiFactory();
        using var client = factory.CreateApiClient(apiKey: null);

        using var response = await client.PostAsJsonAsync(Route, new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<TokenizedPayloadDto> DecryptAsync(PaymentDemoApiFactory factory, string encrypted)
    {
        using var scope = factory.Services.CreateScope();
        var decryption = scope.ServiceProvider.GetRequiredService<IPayloadDecryptionService>();
        Assert.IsType<PayloadDecryptionService>(decryption);
        return Assert.IsType<TokenizedPayloadDto>(await decryption.DecryptAsync(new DecryptPayloadRequest { EncryptedPayload = encrypted }));
    }
}
