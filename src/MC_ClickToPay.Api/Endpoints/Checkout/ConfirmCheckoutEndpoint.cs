using FastEndpoints;
using FluentValidation;
using MC_ClickToPay.Services.Abstractions;
using MC_ClickToPay.Services.Checkout;

namespace MC_ClickToPay.Api.Endpoints.Checkout;

public sealed class ConfirmCheckoutEndpoint(IMastercardCheckoutService checkout, ILogger<ConfirmCheckoutEndpoint> logger)
    : Endpoint<CheckoutConfirmationRequest>
{
    public override void Configure()
    {
        Post("/api/checkout/confirmations");
        Summary(s =>
        {
            s.Summary = "Confirm a Click to Pay checkout";
            s.Description = "Reports the authorization result to Mastercard POST /srci/api/checkout/confirmations.";
            s.Response(204, "Confirmation accepted by Mastercard.");
            s.Response(400, "Invalid request.");
            s.Response(401, "API key is missing, incorrect or not configured.");
            s.Response(502, "Mastercard rejected the call or could not be reached.");
            s.Response(503, "Signing key is not configured or unavailable.");
        });
    }

    public override async Task HandleAsync(CheckoutConfirmationRequest request, CancellationToken ct)
    {
        try
        {
            await checkout.ConfirmAsync(request, ct);
            await Send.NoContentAsync(ct);
        }
        catch (Exception ex) when (CheckoutErrors.ToProblem(ex, logger) is { } problem)
        {
            await Send.ResultAsync(problem);
        }
    }
}

public sealed class ConfirmCheckoutValidator : Validator<CheckoutConfirmationRequest>
{
    public ConfirmCheckoutValidator()
    {
        RuleFor(x => x.CorrelationId).NotEmpty().MaximumLength(256);
        RuleFor(x => x.MerchantTransactionId).NotEmpty().MaximumLength(256);
        RuleFor(x => x.TransactionAmount).GreaterThan(0);
        RuleFor(x => x.TransactionCurrencyCode).NotEmpty().Length(3);
    }
}
