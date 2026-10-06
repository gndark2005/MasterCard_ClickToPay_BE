using System.Text.Json.Serialization;

namespace MC_ClickToPay.Services.Models;

/// <summary>
/// Tokenized DSRP payload: network token + cryptogram in dynamicData (dynamicDataType
/// CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM, no card object).
/// </summary>
public sealed class TokenizedPayloadDto : DecryptedPayloadDto
{
    [JsonPropertyName("token")]
    public PaymentTokenDto? Token { get; init; }

    public override string? GetCredentialCardholderName() => Token?.CardholderFullName;

    public override string? GetCredentialPaymentAccountReference() => Token?.PaymentAccountReference;
}
