using System.Security.Cryptography;
using Jose;
using MC_ClickToPay.Services.Abstractions;
using MC_ClickToPay.Services.Exceptions;
using MC_ClickToPay.Services.Models;
using Xunit;

namespace MC_ClickToPay.Services.Tests;

public sealed class PayloadDecryptionServiceTests
{
    private const string Payload = """
        {"token":{"paymentToken":"5480983179133165","tokenExpirationMonth":"07","tokenExpirationYear":"30"},
         "dynamicData":{"dynamicDataValue":"test-cryptogram","dynamicDataType":"CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM"},
         "srcTokenResultsData":{"tokenRequesterId":"50123197928","unpredictableNumber":"63f43cff"},
         "consumerMobileNumber":{"countryCode":"1","phoneNumber":"0000000000"},
         "billingAddress":{"line1":"Test address","countryCode":"US"}}
        """;

    [Fact]
    public async Task DecryptsAndMapsTokenizedPayload()
    {
        using var key = RSA.Create(2048);
        var result = Assert.IsType<TokenizedPayloadDto>(await Service(key).DecryptAsync(Request(Encrypt(Payload, key))));
        Assert.Equal("5480983179133165", result.Token!.PaymentToken);
        Assert.Equal("07", result.Token.TokenExpirationMonth);
        Assert.Equal("30", result.Token.TokenExpirationYear);
        Assert.Equal("test-cryptogram", result.DynamicData!.DynamicDataValue);
        Assert.Equal("50123197928", result.SrcTokenResultsData!.TokenRequesterId);
        Assert.Equal("US", result.BillingAddress!.CountryCode);
        Assert.Equal("0000000000", result.ConsumerMobileNumber!.PhoneNumber);
        Assert.Null(result.ShippingAddress);
    }

    [Fact]
    public async Task DecryptsFpanPayload()
    {
        using var key = RSA.Create(2048);
        const string fpan = """
            {"consumerEmailAddress":"test1234@dummyemail.com","dynamicData":{"dynamicDataType":"NONE"},
             "billingAddress":{"zip":"10011","city":"New York City","countryCode":"US"},
             "card":{"cardholderFullName":"john doe","panExpirationYear":"2030","primaryAccountNumber":"5120350100064537","panExpirationMonth":"12"}}
            """;

        var result = Assert.IsType<FpanPayloadDto>(await Service(key).DecryptAsync(Request(Encrypt(fpan, key))));

        Assert.Equal("5120350100064537", result.Card!.PrimaryAccountNumber);
        Assert.Equal("12", result.Card.PanExpirationMonth);
        Assert.Equal("NONE", result.DynamicData!.DynamicDataType);
    }

    [Theory]
    [InlineData("""{"card":{"primaryAccountNumber":"5120350100064537"},"dynamicData":{"dynamicDataValue":"637","dynamicDataType":"DYNAMIC_CARD_SECURITY_CODE"}}""")]
    [InlineData("""{"card":{"primaryAccountNumber":"5120350100064537"},"token":{"paymentToken":"************9541"},"dynamicData":{"dynamicDataValue":"AH14E2rQmy6mABQkMkPpAAADFA==","dynamicDataType":"CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM"}}""")]
    public async Task AcceptsPanWithDtvcAndDsrpPlusPan(string content)
    {
        using var key = RSA.Create(2048);

        var result = Assert.IsAssignableFrom<CardPayloadDto>(await Service(key).DecryptAsync(Request(Encrypt(content, key))));

        Assert.Equal("5120350100064537", result.Card!.PrimaryAccountNumber);
    }

    [Fact]
    public async Task RejectsUnsupportedDynamicDataTypeAsInvalidPayload()
    {
        using var key = RSA.Create(2048);
        const string content = """{"token":{"paymentToken":"5480983179133165"},"dynamicData":{"dynamicDataValue":"x","dynamicDataType":"TAVV"}}""";

        var exception = await Assert.ThrowsAsync<PayloadDecryptionException>(() =>
            Service(key).DecryptAsync(Request(Encrypt(content, key))));

        Assert.Equal(PayloadDecryptionError.InvalidPayload, exception.Error);
    }

    [Theory]
    [InlineData("""{"card":{"primaryAccountNumber":"5120350100064537"},"dynamicData":{"dynamicDataType":"CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM"}}""")]
    [InlineData("""{"card":{"primaryAccountNumber":"5120350100064537"},"dynamicData":{"dynamicDataType":"DYNAMIC_CARD_SECURITY_CODE"}}""")]
    [InlineData("""{"token":{"paymentToken":"5480983179133165"},"dynamicData":{"dynamicDataType":"NONE"}}""")]
    public async Task RejectsCredentialWithMismatchedDynamicDataType(string content)
    {
        using var key = RSA.Create(2048);
        var exception = await Assert.ThrowsAsync<PayloadDecryptionException>(() =>
            Service(key).DecryptAsync(Request(Encrypt(content, key))));
        Assert.Equal(PayloadDecryptionError.InvalidPayload, exception.Error);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task RejectsModifiedCiphertextOrAuthenticationTag(int segment)
    {
        using var key = RSA.Create(2048);
        var parts = Encrypt(Payload, key).Split('.');
        var bytes = Base64Url.Decode(parts[segment]);
        bytes[0] ^= 1;
        parts[segment] = Base64Url.Encode(bytes);
        var exception = await Assert.ThrowsAsync<PayloadDecryptionException>(() =>
            Service(key).DecryptAsync(Request(string.Join('.', parts))));
        Assert.Equal(PayloadDecryptionError.DecryptionFailed, exception.Error);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task RejectsWrongPrivateKey()
    {
        using var sender = RSA.Create(2048);
        using var receiver = RSA.Create(2048);
        var exception = await Assert.ThrowsAsync<PayloadDecryptionException>(() =>
            Service(receiver).DecryptAsync(Request(Encrypt(Payload, sender))));
        Assert.Equal(PayloadDecryptionError.DecryptionFailed, exception.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("header.payload.signature")]
    [InlineData("!.a.b.c.d")]
    public async Task RejectsInvalidInputWithoutResolvingKey(string input)
    {
        var exception = await Assert.ThrowsAsync<PayloadDecryptionException>(() =>
            new PayloadDecryptionService(new UnavailableKeyProvider()).DecryptAsync(Request(input)));
        Assert.Equal(PayloadDecryptionError.InvalidInput, exception.Error);
    }

    [Fact]
    public async Task RejectsOversizedInputBeforeResolvingKey()
    {
        var exception = await Assert.ThrowsAsync<PayloadDecryptionException>(() =>
            new PayloadDecryptionService(new UnavailableKeyProvider()).DecryptAsync(
                Request(new string('a', PayloadDecryptionService.MaximumPayloadLength + 1))));
        Assert.Equal(PayloadDecryptionError.InvalidInput, exception.Error);
    }

    [Fact]
    public async Task RejectsUnapprovedAlgorithmBeforeResolvingKey()
    {
        using var key = RSA.Create(2048);
        var encrypted = JWT.Encode(Payload, key, JweAlgorithm.RSA_OAEP_256, JweEncryption.A256GCM);
        var exception = await Assert.ThrowsAsync<PayloadDecryptionException>(() =>
            new PayloadDecryptionService(new UnavailableKeyProvider()).DecryptAsync(Request(encrypted)));
        Assert.Equal(PayloadDecryptionError.UnsupportedHeader, exception.Error);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task RejectsInvalidDecryptedContent(string content)
    {
        using var key = RSA.Create(2048);
        var exception = await Assert.ThrowsAsync<PayloadDecryptionException>(() =>
            Service(key).DecryptAsync(Request(Encrypt(content, key))));
        Assert.Equal(PayloadDecryptionError.InvalidPayload, exception.Error);
    }

    [Fact]
    public async Task HonorsCancellationBeforeResolvingKey()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PayloadDecryptionService(new UnavailableKeyProvider())
                .DecryptAsync(Request("unused"), cancellation.Token));
    }

    private static string Encrypt(string json, RSA key) =>
        JWT.Encode(json, key, JweAlgorithm.RSA_OAEP_256, JweEncryption.A128CBC_HS256);

    private static DecryptPayloadRequest Request(string value) => new() { EncryptedPayload = value };

    private static PayloadDecryptionService Service(RSA key) => new(new TestKeyProvider(key.ExportParameters(true)));
}
