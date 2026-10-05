using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Jose;
using MC_ClickToPay.Services.Payments;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MC_ClickToPay.PaymentDemo.Api.Tests;

public sealed class ConfirmPaymentEndpointTests
{
    private const string Route = "/api/demo/payments/confirm";

    [Fact]
    public async Task ValidEncryptedPayloadReturnsSimulatedApprovedConfirmation()
    {
        using var factory = new PaymentDemoApiFactory();
        using var client = factory.CreateApiClient();
        var encrypted = await PaymentDemoApiFactory.CreateSamplePayloadAsync(client);

        using var response = await client.PostAsJsonAsync(Route, Body(encrypted));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var text = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(text).RootElement;
        Assert.Equal("Approved", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("approved").GetBoolean());
        Assert.True(body.GetProperty("simulated").GetBoolean());
        Assert.Equal("Simulated", body.GetProperty("processor").GetString());
        Assert.Equal("ORDER-1001", body.GetProperty("orderId").GetString());
        Assert.Equal("00", body.GetProperty("responseCode").GetString());
        Assert.Matches("^[0-9]{6}$", body.GetProperty("authorizationCode").GetString());
        Assert.True(Guid.TryParse(body.GetProperty("transactionId").GetString(), out _));
        Assert.Equal(6.00m, body.GetProperty("transactionAmount").GetDecimal());
        Assert.Equal("USD", body.GetProperty("transactionCurrencyCode").GetString());
        Assert.Equal("3165", body.GetProperty("tokenLast4").GetString());
        AssertNoPaymentData(text);
        Assert.All(factory.Logs.Messages, AssertNoPaymentData);
        Assert.Contains(factory.Logs.Messages, m => m.Contains("ORDER-1001") && m.Contains("3165"));
    }

    [Fact]
    public async Task GeneratesOrderIdWhenOmitted()
    {
        using var factory = new PaymentDemoApiFactory();
        using var client = factory.CreateApiClient();
        var encrypted = await PaymentDemoApiFactory.CreateSamplePayloadAsync(client);

        using var response = await client.PostAsJsonAsync(Route, Body(encrypted, orderId: null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("ORD-", body.GetProperty("orderId").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingEncryptedPayloadReturnsBadRequest(string? encryptedPayload)
    {
        using var factory = new PaymentDemoApiFactory();
        using var client = factory.CreateApiClient();

        using var response = await client.PostAsJsonAsync(Route, Body(encryptedPayload));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "missing_payload");
    }

    [Fact]
    public async Task EmptyJsonBodyReturnsMissingPayload()
    {
        using var factory = new PaymentDemoApiFactory();
        using var client = factory.CreateApiClient();

        using var response = await client.PostAsJsonAsync(Route, new { });

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "missing_payload");
    }

    [Theory]
    [InlineData(null, "USD", "ORDER-1", "transactionAmount is required")]
    [InlineData("0", "USD", "ORDER-1", "transactionAmount must be")]
    [InlineData("6.005", "USD", "ORDER-1", "transactionAmount must be")]
    [InlineData("6.00", null, "ORDER-1", "transactionCurrencyCode is required")]
    [InlineData("6.00", "usd", "ORDER-1", "transactionCurrencyCode must be")]
    [InlineData("6.00", "US1", "ORDER-1", "transactionCurrencyCode must be")]
    [InlineData("6.00", "USD", "bad order id", "orderId must be")]
    public async Task InvalidRequestFieldsReturnBadRequestWithFieldErrors(
        string? amount, string? currency, string orderId, string expectedError)
    {
        using var factory = new PaymentDemoApiFactory();
        using var client = factory.CreateApiClient();
        var request = Body("a.b.c.d.e", amount is null ? null : decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture),
            currency, orderId);

        using var response = await client.PostAsJsonAsync(Route, request);

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Contains(problem.GetProperty("errors").EnumerateArray(), e => e.GetString()!.StartsWith(expectedError));
    }

    [Theory]
    [InlineData("not-a-jwe")]
    [InlineData("header.payload.signature")]
    [InlineData("eyJhbGciOiJSU0ExXzUiLCJlbmMiOiJBMTI4Q0JDLUhTMjU2In0.a.b.c.d")]
    public async Task MalformedOrUnsupportedEncryptedPayloadReturnsInvalidPayload(string encryptedPayload)
    {
        using var factory = new PaymentDemoApiFactory();
        using var client = factory.CreateApiClient();

        using var response = await client.PostAsJsonAsync(Route, Body(encryptedPayload));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid_payload");
    }

    [Fact]
    public async Task CorruptedEncryptedPayloadReturnsDecryptionFailedWithoutCryptoDetails()
    {
        using var factory = new PaymentDemoApiFactory();
        using var client = factory.CreateApiClient();
        var parts = (await PaymentDemoApiFactory.CreateSamplePayloadAsync(client)).Split('.');
        var tag = Base64Url.Decode(parts[4]);
        tag[0] ^= 1;
        parts[4] = Base64Url.Encode(tag);

        using var response = await client.PostAsJsonAsync(Route, Body(string.Join('.', parts)));

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "decryption_failed");
        var text = problem.GetRawText();
        Assert.DoesNotContain("Jose", text);
        Assert.DoesNotContain("Cryptographic", text);
        AssertNoPaymentData(text);
    }

    [Fact]
    public async Task PayloadEncryptedForAnotherKeyReturnsDecryptionFailed()
    {
        using var factory = new PaymentDemoApiFactory();
        using var client = factory.CreateApiClient();
        using var otherKey = RSA.Create(2048);
        var encrypted = JWT.Encode(ValidPayloadJson, otherKey, JweAlgorithm.RSA_OAEP_256, JweEncryption.A128CBC_HS256);

        using var response = await client.PostAsJsonAsync(Route, Body(encrypted));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "decryption_failed");
    }

    [Theory]
    [InlineData("""{"token":{"paymentToken":"5480983179133165","tokenExpirationMonth":"12","tokenExpirationYear":"2030"}}""")]
    [InlineData("""{"dynamicData":{"dynamicDataValue":"x","dynamicDataType":"CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM"}}""")]
    [InlineData("""{"token":"not-an-object"}""")]
    [InlineData("not json at all")]
    public async Task DecryptedPayloadWithoutRequiredStructureReturnsUnprocessable(string decryptedJson)
    {
        using var factory = new PaymentDemoApiFactory();
        using var client = factory.CreateApiClient();

        using var response = await client.PostAsJsonAsync(Route, Body(factory.Encrypt(decryptedJson)));

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "invalid_payment_data");
    }

    [Fact]
    public async Task DecryptedPayloadWithInvalidPaymentDataReturnsFieldErrorsWithoutValues()
    {
        using var factory = new PaymentDemoApiFactory();
        using var client = factory.CreateApiClient();
        var encrypted = await PaymentDemoApiFactory.CreateSamplePayloadAsync(client,
            new { paymentToken = "5480abc179133165", tokenExpirationMonth = "13", tokenExpirationYear = "2020" });

        using var response = await client.PostAsJsonAsync(Route, Body(encrypted));

        var problem = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "invalid_payment_data");
        var errors = problem.GetProperty("errors").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Contains(errors, e => e.StartsWith("token.paymentToken"));
        Assert.Contains(errors, e => e.StartsWith("token.tokenExpirationMonth"));
        Assert.DoesNotContain("5480abc179133165", problem.GetRawText());
        Assert.All(factory.Logs.Messages, m => Assert.DoesNotContain("5480abc179133165", m));
    }

    [Fact]
    public async Task ExpiredTokenReturnsUnprocessable()
    {
        using var factory = new PaymentDemoApiFactory();
        using var client = factory.CreateApiClient();
        var encrypted = await PaymentDemoApiFactory.CreateSamplePayloadAsync(client,
            new { tokenExpirationMonth = "01", tokenExpirationYear = "2020" });

        using var response = await client.PostAsJsonAsync(Route, Body(encrypted));

        var problem = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "invalid_payment_data");
        Assert.Contains(problem.GetProperty("errors").EnumerateArray(), e => e.GetString()!.StartsWith("token is expired"));
    }

    [Fact]
    public async Task SimulatedDeclineReturnsDeclinedConfirmation()
    {
        using var factory = new PaymentDemoApiFactory();
        factory.Settings["PaymentDemo:Simulation:Outcome"] = "Declined";
        using var client = factory.CreateApiClient();
        var encrypted = await PaymentDemoApiFactory.CreateSamplePayloadAsync(client);

        using var response = await client.PostAsJsonAsync(Route, Body(encrypted));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Declined", body.GetProperty("status").GetString());
        Assert.False(body.GetProperty("approved").GetBoolean());
        Assert.Equal("05", body.GetProperty("responseCode").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("authorizationCode").ValueKind);
    }

    [Fact]
    public async Task SimulatedProcessorFailureReturnsBadGateway()
    {
        using var factory = new PaymentDemoApiFactory();
        factory.Settings["PaymentDemo:Simulation:Outcome"] = "Failure";
        using var client = factory.CreateApiClient();
        var encrypted = await PaymentDemoApiFactory.CreateSamplePayloadAsync(client);

        using var response = await client.PostAsJsonAsync(Route, Body(encrypted));

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadGateway, "payment_processing_failed");
        AssertNoPaymentData(problem.GetRawText());
    }

    [Fact]
    public async Task UnexpectedProcessorErrorReturnsInternalServerErrorWithoutDetails()
    {
        using var factory = new PaymentDemoApiFactory
        {
            TestServices = services => services.AddScoped<IPaymentProcessor, ThrowingPaymentProcessor>()
        };
        using var client = factory.CreateApiClient();
        var encrypted = await PaymentDemoApiFactory.CreateSamplePayloadAsync(client);

        using var response = await client.PostAsJsonAsync(Route, Body(encrypted));

        var problem = await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "unexpected_error");
        Assert.DoesNotContain(ThrowingPaymentProcessor.Message, problem.GetRawText());
        Assert.All(factory.Logs.Messages, m => Assert.DoesNotContain(ThrowingPaymentProcessor.Message, m));
    }

    [Fact]
    public async Task MissingDecryptionKeyReturnsServiceUnavailable()
    {
        using var factory = new PaymentDemoApiFactory();
        factory.Settings["PayloadEncryption:UseEphemeralDevelopmentKey"] = "false";
        using var client = factory.CreateApiClient();
        using var anyKey = RSA.Create(2048);
        var encrypted = JWT.Encode(ValidPayloadJson, anyKey, JweAlgorithm.RSA_OAEP_256, JweEncryption.A128CBC_HS256);

        using var response = await client.PostAsJsonAsync(Route, Body(encrypted));

        await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "decryption_unavailable");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("incorrect-key")]
    public async Task MissingOrInvalidApiKeyReturnsUnauthorized(string? apiKey)
    {
        using var factory = new PaymentDemoApiFactory();
        using var client = factory.CreateApiClient(apiKey);

        using var response = await client.PostAsJsonAsync(Route, Body("a.b.c.d.e"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private const string ValidPayloadJson = """
        {"token":{"paymentToken":"5480983179133165","tokenExpirationMonth":"12","tokenExpirationYear":"2030"},
         "dynamicData":{"dynamicDataValue":"DEMOcryptogramNOTREAL000000=","dynamicDataType":"CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM"}}
        """;

    private static object Body(string? encryptedPayload, decimal? amount = 6.00m, string? currency = "USD",
        string? orderId = "ORDER-1001") => new
        {
            encryptedPayload,
            transactionAmount = amount,
            transactionCurrencyCode = currency,
            orderId
        };

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        return problem;
    }

    private static void AssertNoPaymentData(string text)
    {
        Assert.DoesNotContain(PaymentDemoApiFactory.ConfiguredToken, text);
        Assert.DoesNotContain(PaymentDemoApiFactory.ConfiguredCryptogram, text);
    }

    private sealed class ThrowingPaymentProcessor : IPaymentProcessor
    {
        public const string Message = "internal failure for token 5480983179133165";

        public string Name => "Throwing";

        public bool IsSimulated => true;

        public Task<PaymentResult> ProcessAsync(PaymentRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(Message);
    }
}
