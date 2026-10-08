using System.Text.Json;
using MC_ClickToPay.Services.Checkout;
using MC_ClickToPay.Services.Models;
using MC_ClickToPay.Services.Payments;
using Xunit;

namespace MC_ClickToPay.Services.Tests;

public sealed class CheckoutPanPayloadDtoTests
{
    [Fact]
    public void TokenOnlyPayloadFillsCardFromTheTokenAndKeepsIt()
    {
        var source = Deserialize("""
            {"token":{"paymentToken":"5186051929138756","tokenExpirationMonth":"09","tokenExpirationYear":"2029",
                      "paymentAccountReference":"500195ZNPA990U50U70OOXLT8AWXT","cardholderFullName":"Adelle Ryan"},
             "dynamicData":{"dynamicDataValue":"AAnIdMIzlaW+ACJyvravAAADFA==","dynamicDataType":"CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM"},
             "billingAddress":{"line1":"8373 Maineville Road","city":"Maineville","state":"OH","countryCode":"US","zip":"45039"},
             "consumerEmailAddress":"john.doe73@mailnator.com"}
            """);

        var payload = CheckoutPanPayloadDto.From(source);

        Assert.Equal(PaymentCredentialType.NetworkToken, payload.CredentialType);
        Assert.Equal("5186051929138756", payload.Card!.PrimaryAccountNumber);
        Assert.Equal("09", payload.Card.PanExpirationMonth);
        Assert.Equal("2029", payload.Card.PanExpirationYear);
        Assert.Equal("Adelle Ryan", payload.Card.CardholderFullName);
        Assert.Equal("500195ZNPA990U50U70OOXLT8AWXT", payload.Card.PaymentAccountReference);
        Assert.Equal("5186051929138756", payload.Token!.PaymentToken);
        Assert.Equal("AAnIdMIzlaW+ACJyvravAAADFA==", payload.DynamicData!.DynamicDataValue);
        Assert.Equal("Maineville", payload.BillingAddress!.City);
        Assert.Equal("john.doe73@mailnator.com", payload.ConsumerEmailAddress);
    }

    [Fact]
    public void DsrpPlusPanKeepsThePanAndTheMaskedToken()
    {
        var source = Deserialize("""
            {"card":{"primaryAccountNumber":"5120350100064537","panExpirationMonth":"07","panExpirationYear":"2029","cardholderFullName":"John Doe"},
             "token":{"paymentToken":"************9541","tokenExpirationMonth":"08","tokenExpirationYear":"2027"},
             "dynamicData":{"dynamicDataValue":"AH14E2rQmy6mABQkMkPpAAADFA==","dynamicDataType":"CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM"}}
            """);

        var payload = CheckoutPanPayloadDto.From(source);

        Assert.Equal(PaymentCredentialType.Pan, payload.CredentialType);
        Assert.Equal("5120350100064537", payload.Card!.PrimaryAccountNumber);
        Assert.Equal("07", payload.Card.PanExpirationMonth);
        Assert.Equal("************9541", payload.Token!.PaymentToken);
    }

    [Theory]
    [InlineData("""{"card":{"primaryAccountNumber":"5120350100064537","panExpirationMonth":"12","panExpirationYear":"2030"},"dynamicData":{"dynamicDataValue":"637","dynamicDataType":"DYNAMIC_CARD_SECURITY_CODE"}}""")]
    [InlineData("""{"card":{"primaryAccountNumber":"5120350100064537","panExpirationMonth":"12","panExpirationYear":"2030"},"dynamicData":{"dynamicDataType":"NONE"}}""")]
    public void CardPayloadsKeepTheCardAndHaveNoToken(string json)
    {
        var payload = CheckoutPanPayloadDto.From(Deserialize(json));

        Assert.Equal(PaymentCredentialType.Pan, payload.CredentialType);
        Assert.Equal("5120350100064537", payload.Card!.PrimaryAccountNumber);
        Assert.Null(payload.Token);
    }

    [Fact]
    public void SerializesCardAndTokenButNotTheInternalCredentialType()
    {
        var payload = CheckoutPanPayloadDto.From(Deserialize("""
            {"token":{"paymentToken":"5186051929138756","tokenExpirationMonth":"09","tokenExpirationYear":"2029"},
             "dynamicData":{"dynamicDataValue":"x","dynamicDataType":"CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM"}}
            """));

        var json = JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;

        Assert.Equal("5186051929138756", json.GetProperty("card").GetProperty("primaryAccountNumber").GetString());
        Assert.Equal("5186051929138756", json.GetProperty("token").GetProperty("paymentToken").GetString());
        Assert.False(json.TryGetProperty("CredentialType", out _));
    }

    private static DecryptedPayloadDto Deserialize(string json) => JsonSerializer.Deserialize<DecryptedPayloadDto>(json)!;
}
