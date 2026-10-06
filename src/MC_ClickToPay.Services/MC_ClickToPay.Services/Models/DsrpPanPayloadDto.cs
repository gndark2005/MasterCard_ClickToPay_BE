using System.Text.Json.Serialization;

namespace MC_ClickToPay.Services.Models;

/// <summary>
/// DSRP + PAN payload: PAN in card, DSRP cryptogram in dynamicData (CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM) and the
/// token, usually masked (e.g. ************9541). With an unmasked token it is a dual payload.
/// </summary>
public sealed class DsrpPanPayloadDto : CardPayloadDto
{
    [JsonPropertyName("token")]
    public PaymentTokenDto? Token { get; init; }

    public override string? GetCredentialCardholderName() => Card?.CardholderFullName ?? Token?.CardholderFullName;

    public override string? GetCredentialPaymentAccountReference() =>
        Card?.PaymentAccountReference ?? Token?.PaymentAccountReference;
}
