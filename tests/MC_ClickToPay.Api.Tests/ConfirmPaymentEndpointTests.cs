using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Jose;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MC_ClickToPay.Api.Tests;

public sealed class ConfirmPaymentEndpointTests
{
    private const string Route = "/api/payments/confirm";
    private const string Token = "5480983179133165";
    private const string Cryptogram = "DEMOcryptogramNOTREAL000000=";

    // Mastercard sandbox payload shape (fake values).
    private const string PayloadJson = """
        {"consumerEmailAddress":"jane.demo@example.com",
         "dynamicData":{"dynamicDataValue":"DEMOcryptogramNOTREAL000000=","dynamicDataType":"CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM"},
         "shippingAddress":{"zip":"10038","city":"New York","countryCode":"US","name":"Jane Demo","state":"NY","line1":"456 Demo Avenue"},
         "billingAddress":{"zip":"99950","city":"Denver","countryCode":"US","state":"CO","line1":"123 Demo Street"},
         "consumerMobileNumber":{"phoneNumber":"5550100","countryCode":"1"},
         "token":{"paymentToken":"5480983179133165","paymentAccountReference":"DEMO0000000000000000000000001",
                  "tokenExpirationMonth":"01","cardholderFullName":"Jane Demo","tokenExpirationYear":"2099"}}
        """;

    [Fact]
    public async Task ConfirmsMastercardPayloadWithSimulatedApproval()
    {
        using var factory = new PayloadApiFactory();
        using var client = CreateClient(factory);

        using var response = await client.PostAsJsonAsync(Route, Body(Encrypt(factory.EncryptionKey, PayloadJson)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(text).RootElement;
        Assert.Equal("Approved", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("simulated").GetBoolean());
        Assert.Equal("ORDER-5", body.GetProperty("orderId").GetString());
        Assert.Equal(31.25m, body.GetProperty("transactionAmount").GetDecimal());
        Assert.Equal("3165", body.GetProperty("tokenLast4").GetString());
        Assert.Equal("06", body.GetProperty("eci").GetString());
        Assert.DoesNotContain(Token, text);
        Assert.DoesNotContain(Cryptogram, text);
    }

    [Fact]
    public async Task SimulatedDeclineIsConfigurable()
    {
        using var baseFactory = new PayloadApiFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(
            (_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["PaymentSimulation:Outcome"] = "Declined" })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("X-Api-Key", PayloadApiFactory.ApiKey);

        using var response = await client.PostAsJsonAsync(Route, Body(Encrypt(baseFactory.EncryptionKey, PayloadJson)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("approved").GetBoolean());
        Assert.Equal("05", body.GetProperty("responseCode").GetString());
    }

    [Theory]
    [InlineData(null, "missing_payload")]
    [InlineData("not-a-jwe", "invalid_payload")]
    public async Task RejectsMissingOrMalformedPayload(string? encryptedPayload, string code)
    {
        using var factory = new PayloadApiFactory();
        using var client = CreateClient(factory);

        using var response = await client.PostAsJsonAsync(Route, Body(encryptedPayload));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, code);
    }

    [Fact]
    public async Task RejectsInvalidEci()
    {
        using var factory = new PayloadApiFactory();
        using var client = CreateClient(factory);

        using var response = await client.PostAsJsonAsync(Route, Body("a.b.c.d.e", eci: "6"));

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Contains(problem.GetProperty("errors").EnumerateArray(), e => e.GetString()!.StartsWith("eci"));
    }

    [Fact]
    public async Task PayloadForAnotherKeyIsDecryptionFailed()
    {
        using var factory = new PayloadApiFactory();
        using var client = CreateClient(factory);
        using var otherKey = RSA.Create(2048);

        using var response = await client.PostAsJsonAsync(Route, Body(Encrypt(otherKey, PayloadJson)));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "decryption_failed");
    }

    [Fact]
    public async Task ExpiredTokenIsInvalidPaymentData()
    {
        using var factory = new PayloadApiFactory();
        using var client = CreateClient(factory);
        var expired = PayloadJson.Replace("\"2099\"", "\"2020\"");

        using var response = await client.PostAsJsonAsync(Route, Body(Encrypt(factory.EncryptionKey, expired)));

        var problem = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "invalid_payment_data");
        Assert.DoesNotContain(Token, problem.GetRawText());
    }

    [Fact]
    public async Task UnreadableCertificateIsServiceUnavailable()
    {
        using var factory = new PayloadApiFactory { MissingCertificate = true };
        using var client = CreateClient(factory);

        using var response = await client.PostAsJsonAsync(Route, Body(Encrypt(factory.EncryptionKey, PayloadJson)));

        await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "decryption_unavailable");
    }

    [Fact]
    public async Task RequiresApiKey()
    {
        using var factory = new PayloadApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        using var response = await client.PostAsJsonAsync(Route, Body("a.b.c.d.e"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static HttpClient CreateClient(PayloadApiFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("X-Api-Key", PayloadApiFactory.ApiKey);
        return client;
    }

    private static string Encrypt(RSA key, string json) =>
        JWT.Encode(json, key, JweAlgorithm.RSA_OAEP_256, JweEncryption.A128CBC_HS256);

    private static object Body(string? encryptedPayload, string? eci = "06") => new
    {
        encryptedPayload,
        transactionAmount = 31.25m,
        transactionCurrencyCode = "USD",
        orderId = "ORDER-5",
        eci
    };

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
        return problem;
    }
}
