using System.Text.Json.Serialization;
using MC_ClickToPay.Services.Models;
using MC_ClickToPay.Services.Payments;

namespace MC_ClickToPay.Services.Checkout;

/// <summary>
/// The decrypted payload as returned to the caller of /api/checkout: every decrypted field, always with a
/// card object. When Mastercard sent only a token, card is filled from the token (the network token number, not the
/// real PAN: <see cref="CredentialType"/> says which one it is). The token is kept when it was sent.
/// </summary>
public sealed class CheckoutPanPayloadDto : CardPayloadDto
{
    [JsonPropertyName("token")]
    public PaymentTokenDto? Token { get; init; }

    /// <summary>Pan when card came in the payload; NetworkToken when card was filled from the token.</summary>
    [JsonIgnore]
    public PaymentCredentialType CredentialType { get; init; }

    public static CheckoutPanPayloadDto From(DecryptedPayloadDto source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var token = source switch
        {
            TokenizedPayloadDto tokenized => tokenized.Token,
            DsrpPanPayloadDto dsrpPan => dsrpPan.Token,
            _ => null
        };
        var card = (source as CardPayloadDto)?.Card;
        var fromToken = card is null && token is not null;

        return new CheckoutPanPayloadDto
        {
            Card = fromToken
                ? new PaymentCardDto
                {
                    PrimaryAccountNumber = token!.PaymentToken,
                    PanExpirationMonth = token.TokenExpirationMonth,
                    PanExpirationYear = token.TokenExpirationYear,
                    CardholderFullName = token.CardholderFullName,
                    PaymentAccountReference = token.PaymentAccountReference
                }
                : card,
            Token = token,
            CredentialType = fromToken ? PaymentCredentialType.NetworkToken : PaymentCredentialType.Pan,
            DynamicData = source.DynamicData,
            SrcTokenResultsData = source.SrcTokenResultsData,
            ShippingAddress = source.ShippingAddress,
            BillingAddress = source.BillingAddress,
            ConsumerEmailAddress = source.ConsumerEmailAddress,
            ConsumerFirstName = source.ConsumerFirstName,
            ConsumerLastName = source.ConsumerLastName,
            ConsumerFullName = source.ConsumerFullName,
            ConsumerMobileNumber = source.ConsumerMobileNumber
        };
    }
}
