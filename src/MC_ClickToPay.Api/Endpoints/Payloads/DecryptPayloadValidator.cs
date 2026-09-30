using FastEndpoints;
using FluentValidation;
using MC_ClickToPay.Services;
using MC_ClickToPay.Services.Models;

namespace MC_ClickToPay.Api.Endpoints.Payloads;

public sealed class DecryptPayloadValidator : Validator<DecryptPayloadRequest>
{
    public DecryptPayloadValidator()
    {
        RuleFor(x => x.EncryptedPayload)
            .NotEmpty()
            .MaximumLength(PayloadDecryptionService.MaximumPayloadLength);
    }
}
