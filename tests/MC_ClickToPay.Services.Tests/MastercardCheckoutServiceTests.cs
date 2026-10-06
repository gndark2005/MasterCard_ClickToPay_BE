using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Jose;
using MC_ClickToPay.Services.Abstractions;
using MC_ClickToPay.Services.Checkout;
using MC_ClickToPay.Services.Exceptions;
using MC_ClickToPay.Services.Models;
using Microsoft.Extensions.Options;
using Xunit;

namespace MC_ClickToPay.Services.Tests;

public sealed class MastercardCheckoutServiceTests
{
    private const string Payload = """
        {"token":{"paymentToken":"5185600649952671","tokenExpirationMonth":"01","tokenExpirationYear":"2029",
                  "cardholderFullName":"Adelle Ryan"},
         "dynamicData":{"dynamicDataValue":"AN5mxS9FCZE2ACfVeZZbAAADFA==","dynamicDataType":"CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM"}}
        """;

    private static readonly MastercardCheckoutOptions Settings = new()
    {
        BaseUrl = "https://sandbox.api.mastercard.com",
        SrcDpaId = "823ef281-1a2e-4204-a69c-43d355522a35",
        ConsumerKey = "client-id!key-id",
    };

    [Fact]
    public async Task CompleteSendsSignedCheckoutRequestAndDecryptsPayload()
    {
        using var payloadKey = RSA.Create(2048);
        var jwe = JWT.Encode(Payload, payloadKey, JweAlgorithm.RSA_OAEP_256, JweEncryption.A128CBC_HS256);
        var handler = new FakeHandler(HttpStatusCode.OK,
            $$$"""{"merchantTransactionId":"mtid-1","correlationId":"corr-1","encryptedPayload":"{{{jwe}}}","assuranceData":{"eci":"06"}}""");

        var result = await Service(handler, payloadKey).CompleteAsync(new CompleteCheckoutRequest
        {
            CorrelationId = "corr-1",
            MerchantTransactionId = "mtid-1",
            FlowId = "flow-1",
            TransactionAmount = 31.25m,
            TransactionCurrencyCode = "USD",
        });

        Assert.Equal("06", result.Eci);
        var tokenized = Assert.IsType<TokenizedPayloadDto>(result.Payload);
        Assert.Equal("5185600649952671", tokenized.Token!.PaymentToken);
        Assert.Equal("Adelle Ryan", tokenized.Token.CardholderFullName);
        Assert.Equal("AN5mxS9FCZE2ACfVeZZbAAADFA==", result.Payload.DynamicData!.DynamicDataValue);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://sandbox.api.mastercard.com/srci/api/checkout", request.Uri);
        Assert.StartsWith("OAuth ", request.Authorization);
        Assert.Contains("oauth_consumer_key=\"client-id%21key-id\"", request.Authorization);
        Assert.Contains("oauth_signature_method=\"RSA-SHA256\"", request.Authorization);
        Assert.Equal("client-id", request.ClientId);
        Assert.Equal("flow-1", request.FlowId);

        var body = JsonNode.Parse(request.Body)!;
        Assert.Equal(Settings.SrcDpaId, body["srcDpaId"]!.GetValue<string>());
        Assert.Equal("corr-1", body["correlationId"]!.GetValue<string>());
        Assert.Equal("CLICK_TO_PAY", body["checkoutType"]!.GetValue<string>());
        Assert.Equal("mtid-1", body["checkoutReference"]!["data"]!["merchantTransactionId"]!.GetValue<string>());
        Assert.Equal("31.25", body["dpaTransactionOptions"]!["transactionAmount"]!["transactionAmount"]!.GetValue<string>());
        Assert.Equal("USD", body["dpaTransactionOptions"]!["transactionAmount"]!["transactionCurrencyCode"]!.GetValue<string>());
    }

    [Fact]
    public async Task CompleteReportsMastercardErrorsWithoutTheBody()
    {
        using var payloadKey = RSA.Create(2048);
        var handler = new FakeHandler(HttpStatusCode.BadRequest,
            """{"reason":"INVALID_ARGUMENT","message":"Invalid correlationId","secret":"5185600649952671"}""");

        var exception = await Assert.ThrowsAsync<MastercardCheckoutException>(() =>
            Service(handler, payloadKey).CompleteAsync(new CompleteCheckoutRequest
            {
                CorrelationId = "corr-1",
                MerchantTransactionId = "mtid-1",
                TransactionAmount = 1m,
                TransactionCurrencyCode = "USD",
            }));

        Assert.Equal(400, exception.StatusCode);
        Assert.Contains("INVALID_ARGUMENT", exception.Message);
        Assert.DoesNotContain("5185600649952671", exception.Message);
    }

    [Fact]
    public async Task ConfirmSendsConfirmationStatus()
    {
        using var payloadKey = RSA.Create(2048);
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");

        await Service(handler, payloadKey).ConfirmAsync(new CheckoutConfirmationRequest
        {
            CorrelationId = "corr-1",
            MerchantTransactionId = "mtid-1",
            TransactionAmount = 31.25m,
            TransactionCurrencyCode = "USD",
            Approved = false,
        });

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://sandbox.api.mastercard.com/srci/api/checkout/confirmations", request.Uri);
        var data = JsonNode.Parse(request.Body)!["confirmationData"]!;
        Assert.Equal("02", data["confirmationStatus"]!.GetValue<string>());
        Assert.Equal("31.25", data["transactionAmount"]!["transactionAmount"]!.GetValue<string>());
    }

    private static MastercardCheckoutService Service(FakeHandler handler, RSA payloadKey) => new(
        new HttpClient(handler),
        Options.Create(Settings),
        new TestSigningKeyProvider(),
        new PayloadDecryptionService(new TestKeyProvider(payloadKey.ExportParameters(true))));

    private sealed record CapturedRequest(string Uri, string Authorization, string? ClientId, string? FlowId, string Body);

    private sealed class FakeHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            static string? Header(HttpRequestMessage r, string name) =>
                r.Headers.TryGetValues(name, out var values) ? values.Single() : null;

            Requests.Add(new CapturedRequest(
                request.RequestUri!.ToString(),
                Header(request, "Authorization") ?? string.Empty,
                Header(request, "x-openapi-clientid"),
                Header(request, "x-src-cx-flow-id"),
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(status) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class TestSigningKeyProvider : IMastercardSigningKeyProvider
    {
        public ValueTask<RSA> GetSigningKeyAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(RSA.Create(2048));
    }
}
