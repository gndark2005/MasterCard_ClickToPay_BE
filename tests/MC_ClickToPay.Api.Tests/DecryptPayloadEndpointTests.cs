using System.Net;
using System.Net.Http.Json;
using Jose;
using MC_ClickToPay.Services.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace MC_ClickToPay.Api.Tests;

public sealed class DecryptPayloadEndpointTests
{
    private const string Route = "/api/payloads/decrypt";

    [Fact]
    public async Task DecryptsThroughHttpUsingConfiguredCertificate()
    {
        using var factory = new PayloadApiFactory();
        using var client = CreateClient(factory);
        var encrypted = Encrypt(factory);
        using var response = await client.PostAsJsonAsync(Route, new { encryptedPayload = encrypted });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var payload = Assert.IsType<TokenizedPayloadDto>(await response.Content.ReadFromJsonAsync<DecryptedPayloadDto>());
        Assert.Equal("5480983179133165", payload!.Token!.PaymentToken);
        Assert.Equal("07", payload.Token.TokenExpirationMonth);
        Assert.Equal("test-cryptogram", payload.DynamicData!.DynamicDataValue);
    }

    [Fact]
    public async Task DecryptsWithPemPrivateKeyAsProvidedByMastercard()
    {
        using var factory = new PayloadApiFactory { UsePemKey = true };
        using var client = CreateClient(factory);

        using var response = await client.PostAsJsonAsync(Route, new { encryptedPayload = Encrypt(factory) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = Assert.IsType<TokenizedPayloadDto>(await response.Content.ReadFromJsonAsync<DecryptedPayloadDto>());
        Assert.Equal("5480983179133165", payload!.Token!.PaymentToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("incorrect-key")]
    public async Task RejectsMissingOrInvalidApiKey(string? apiKey)
    {
        using var factory = new PayloadApiFactory();
        using var client = CreateClient(factory, apiKey);
        using var response = await client.PostAsJsonAsync(Route, new { encryptedPayload = "invalid" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("header.payload.signature")]
    public async Task RejectsInvalidPayload(string encryptedPayload)
    {
        using var factory = new PayloadApiFactory();
        using var client = CreateClient(factory);
        using var response = await client.PostAsJsonAsync(Route, new { encryptedPayload });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MissingCertificateReturnsSafeServiceUnavailable()
    {
        using var factory = new PayloadApiFactory { MissingCertificate = true };
        using var client = CreateClient(factory);
        var encrypted = Encrypt(factory);
        using var response = await client.PostAsJsonAsync(Route, new { encryptedPayload = encrypted });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(encrypted, body);
        Assert.DoesNotContain("test-password", body);
    }

    [Fact]
    public async Task RejectsTamperingWithoutReturningCryptographicDetails()
    {
        using var factory = new PayloadApiFactory();
        using var client = CreateClient(factory);
        var parts = Encrypt(factory).Split('.');
        var tag = Base64Url.Decode(parts[4]);
        tag[0] ^= 1;
        parts[4] = Base64Url.Encode(tag);
        using var response = await client.PostAsJsonAsync(Route, new { encryptedPayload = string.Join('.', parts) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("DecryptionFailed", body);
        Assert.DoesNotContain("test-cryptogram", body);
    }

    private static HttpClient CreateClient(PayloadApiFactory factory, string? apiKey = PayloadApiFactory.ApiKey)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        }

        return client;
    }

    private static string Encrypt(PayloadApiFactory factory) => JWT.Encode("""
        {"token":{"paymentToken":"5480983179133165","tokenExpirationMonth":"07","tokenExpirationYear":"30"},
         "dynamicData":{"dynamicDataValue":"test-cryptogram","dynamicDataType":"CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM"}}
        """, factory.EncryptionKey, JweAlgorithm.RSA_OAEP_256, JweEncryption.A128CBC_HS256);
}
