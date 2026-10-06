using System.Text.Json.Serialization;

namespace MC_ClickToPay.Services.Models;

/// <summary>Payloads whose credential is the card object (PAN): FPAN, PAN + DTVC and DSRP + PAN.</summary>
public abstract class CardPayloadDto : DecryptedPayloadDto
{
    [JsonPropertyName("card")]
    public PaymentCardDto? Card { get; init; }

    public override string? GetCredentialCardholderName() => Card?.CardholderFullName;

    public override string? GetCredentialPaymentAccountReference() => Card?.PaymentAccountReference;
}
