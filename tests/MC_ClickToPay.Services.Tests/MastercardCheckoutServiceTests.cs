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
        ConsumerKey = "client-id!key-id",
    };

    [Fact]
    public async Task CompleteSendsSignedCheckoutRequestAndDecryptsPayload()
    {
        using var payloadKey = RSA.Create(2048);
        var jwe = JWT.Encode(Payload, payloadKey, JweAlgorithm.RSA_OAEP_256, JweEncryption.A128CBC_HS256);
        var handler = new FakeHandler(HttpStatusCode.OK,
            $$$"""{"merchantTransactionId":"mtid-1","correlationId":"corr-1","encryptedPayload":"{{{jwe}}}","assuranceData":{"eci":"06"}}""");

        var result = await Service(handler, payloadKey).CompleteAsync(Request());

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
        Assert.Equal("merchant-dpa-1", body["srcDpaId"]!.GetValue<string>());
        Assert.Equal("corr-1", body["correlationId"]!.GetValue<string>());
        Assert.Equal("CLICK_TO_PAY", body["checkoutType"]!.GetValue<string>());
        Assert.Equal("mtid-1", body["checkoutReference"]!["data"]!["merchantTransactionId"]!.GetValue<string>());
        Assert.Equal("31.25", body["dpaTransactionOptions"]!["transactionAmount"]!["transactionAmount"]!.GetValue<string>());
        Assert.Equal("USD", body["dpaTransactionOptions"]!["transactionAmount"]!["transactionCurrencyCode"]!.GetValue<string>());
        Assert.Equal("CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM",
            body["dpaTransactionOptions"]!["paymentOptions"]![0]!["dynamicDataType"]!.GetValue<string>());
        Assert.DoesNotContain("spi-token-1", request.Body);
        Assert.DoesNotContain("x-corr-1", request.Body);
    }

    [Fact]
    public async Task CompleteReportsMastercardErrorsWithoutTheBody()
    {
        using var payloadKey = RSA.Create(2048);
        var handler = new FakeHandler(HttpStatusCode.BadRequest,
            """{"reason":"INVALID_ARGUMENT","message":"Invalid correlationId","secret":"5185600649952671"}""");

        var exception = await Assert.ThrowsAsync<MastercardCheckoutException>(() =>
            Service(handler, payloadKey).CompleteAsync(Request()));

        Assert.Equal(400, exception.StatusCode);
        Assert.Contains("INVALID_ARGUMENT", exception.Message);
        Assert.DoesNotContain("5185600649952671", exception.Message);
    }

    [Fact]
    public async Task ConfirmSendsTheMastercardBodyAndHeadersAndReturnsTheFlowId()
    {
        using var payloadKey = RSA.Create(2048);
        var handler = new FakeHandler(HttpStatusCode.NoContent, string.Empty)
        {
            ResponseFlowId = "flow-from-mastercard"
        };

        var result = await Service(handler, payloadKey).ConfirmAsync(Confirmation());

        Assert.Equal("flow-from-mastercard", result.FlowId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://sandbox.api.mastercard.com/srci/api/checkout/confirmations", request.Uri);
        Assert.StartsWith("OAuth ", request.Authorization);
        Assert.Equal("client-id", request.ClientId);
        Assert.Equal("flow-1", request.FlowId);
        Assert.Equal("0a4e0d3.34f4a04b.bddaa2cf22b394705447f2b03ca662eb86d0b253", request.MerchantTransactionId);

        var body = JsonNode.Parse(request.Body)!;
        Assert.Equal("43465920-50e4-475f-a8d8-198a842fd3bf", body["correlationId"]!.GetValue<string>());
        Assert.Equal("0a4e0d3.34f4a04b.bddaa2cf22b394705447f2b03ca662eb86d0b253", body["merchantTransactionId"]!.GetValue<string>());
        var data = body["confirmationData"]!;
        Assert.Equal("01", data["checkoutEventType"]!.GetValue<string>());
        Assert.Equal("01", data["checkoutEventStatus"]!.GetValue<string>());
        Assert.Equal("01", data["confirmationStatus"]!.GetValue<string>());
        Assert.Equal("The payment was successful", data["confirmationReason"]!.GetValue<string>());
        Assert.Equal("2020-04-25T01:15:00.000Z", data["confirmationTimestamp"]!.GetValue<string>());
        Assert.Equal("2020404", data["networkAuthorizationCode"]!.GetValue<string>());
        Assert.Equal("mastercard", data["networkTransactionIdentifier"]!.GetValue<string>());
        Assert.Equal("1200.00", data["transactionAmount"]!["transactionAmount"]!.GetValue<string>());
        Assert.Equal("USD", data["transactionAmount"]!["transactionCurrencyCode"]!.GetValue<string>());
        Assert.DoesNotContain("x-corr-1", request.Body);
        Assert.Null(body["flowId"]);
    }

    [Fact]
    public async Task ConfirmOmitsOptionalFieldsAndDefaultsTheTimestamp()
    {
        using var payloadKey = RSA.Create(2048);
        var handler = new FakeHandler(HttpStatusCode.NoContent, string.Empty);

        var result = await Service(handler, payloadKey).ConfirmAsync(new CheckoutConfirmationRequest
        {
            CorrelationId = "corr-1",
            FlowId = "flow-1",
            XCorrelationId = "x-corr-1",
            ConfirmationData = new ConfirmationData { CheckoutEventType = "01", CheckoutEventStatus = "01", ConfirmationStatus = "01" }
        });

        Assert.Equal("flow-1", result.FlowId);
        var request = Assert.Single(handler.Requests);
        Assert.Null(request.MerchantTransactionId);
        var body = JsonNode.Parse(request.Body)!;
        Assert.Null(body["merchantTransactionId"]);
        var data = body["confirmationData"]!.AsObject();
        Assert.False(data.ContainsKey("transactionAmount"));
        Assert.False(data.ContainsKey("networkAuthorizationCode"));
        Assert.True(DateTimeOffset.TryParse(data["confirmationTimestamp"]!.GetValue<string>(), out var timestamp));
        Assert.True(DateTimeOffset.UtcNow - timestamp < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task ConfirmReportsMastercardStatusCode()
    {
        using var payloadKey = RSA.Create(2048);
        var handler = new FakeHandler(HttpStatusCode.NotFound,
            """{"errors":[{"reasonCode":"NOT_FOUND","description":"Checkout transaction not found"}]}""");

        var exception = await Assert.ThrowsAsync<MastercardCheckoutException>(() =>
            Service(handler, payloadKey).ConfirmAsync(Confirmation()));

        Assert.Equal(404, exception.StatusCode);
        Assert.Contains("[NOT_FOUND] Checkout transaction not found", exception.Message);
    }

    // Mastercard's request sample.
    private static CheckoutConfirmationRequest Confirmation() => new()
    {
        CorrelationId = "43465920-50e4-475f-a8d8-198a842fd3bf",
        MerchantTransactionId = "0a4e0d3.34f4a04b.bddaa2cf22b394705447f2b03ca662eb86d0b253",
        FlowId = "flow-1",
        XCorrelationId = "x-corr-1",
        ConfirmationData = new ConfirmationData
        {
            CheckoutEventType = "01",
            CheckoutEventStatus = "01",
            ConfirmationStatus = "01",
            ConfirmationReason = "The payment was successful",
            ConfirmationTimestamp = DateTimeOffset.Parse("2020-04-25T01:15:00.000Z", System.Globalization.CultureInfo.InvariantCulture),
            NetworkAuthorizationCode = "2020404",
            NetworkTransactionIdentifier = "mastercard",
            TransactionAmount = new ConfirmationAmount { Amount = 1200m, CurrencyCode = "USD" }
        }
    };

    private static CompleteCheckoutRequest Request() => new()
    {
        SpiToken = "spi-token-1",
        SrcDpaId = "merchant-dpa-1",
        SrcCorrelationId = "corr-1",
        MerchantTransactionId = "mtid-1",
        FlowId = "flow-1",
        TransactionAmount = 31.25m,
        TransactionCurrencyCode = "USD",
        XCorrelationId = "x-corr-1",
    };

    private static MastercardCheckoutService Service(FakeHandler handler, RSA payloadKey) => new(
        new HttpClient(handler),
        Options.Create(Settings),
        new TestSigningKeyProvider(),
        new PayloadDecryptionService(new TestKeyProvider(payloadKey.ExportParameters(true))));

    private sealed record CapturedRequest(
        string Uri, string Authorization, string? ClientId, string? FlowId, string? MerchantTransactionId, string Body);

    private sealed class FakeHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        /// <summary>X-Src-Cx-Flow-Id header of the response, when set.</summary>
        public string? ResponseFlowId { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            static string? Header(HttpRequestMessage r, string name) =>
                r.Headers.TryGetValues(name, out var values) ? values.Single() : null;

            Requests.Add(new CapturedRequest(
                request.RequestUri!.ToString(),
                Header(request, "Authorization") ?? string.Empty,
                Header(request, "x-openapi-clientid"),
                Header(request, "x-src-cx-flow-id"),
                Header(request, "merchant-transaction-id"),
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            var response = new HttpResponseMessage(status) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") };
            if (ResponseFlowId is not null)
            {
                response.Headers.Add("X-Src-Cx-Flow-Id", ResponseFlowId);
            }

            return response;
        }
    }

    private sealed class TestSigningKeyProvider : IMastercardSigningKeyProvider
    {
        public ValueTask<RSA> GetSigningKeyAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(RSA.Create(2048));
    }
}
