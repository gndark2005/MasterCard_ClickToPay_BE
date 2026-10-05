using FastEndpoints;
using FluentValidation;
using MC_ClickToPay.Api.Keys;
using MC_ClickToPay.Services.Abstractions;
using MC_ClickToPay.Services.Checkout;
using MC_ClickToPay.Services.Exceptions;

namespace MC_ClickToPay.Api.Endpoints.Checkout;

public sealed class CompleteCheckoutEndpoint(IMastercardCheckoutService checkout, ILogger<CompleteCheckoutEndpoint> logger)
    : Endpoint<CompleteCheckoutRequest, CompleteCheckoutResult>
{
    public override void Configure()
    {
        Post("/api/checkout/complete");
        Summary(s =>
        {
            s.Summary = "Complete a Click to Pay checkout";
            s.Description = "Takes the srcCorrelationId and merchant-transaction-id returned by checkoutWithCard(), " +
                "calls Mastercard POST /srci/api/checkout (OAuth 1.0a) and decrypts the encryptedPayload. " +
                "Returns the payment token, cryptogram, addresses and the ECI from assuranceData.";
            s.Response<CompleteCheckoutResult>(200, "Decrypted payload and ECI.");
            s.Response(400, "Invalid request or invalid encrypted payload.");
            s.Response(401, "API key is missing, incorrect or not configured.");
            s.Response(502, "Mastercard rejected the call or could not be reached.");
            s.Response(503, "Signing key or decryption certificate is not configured or unavailable.");
        });
    }

    public override async Task HandleAsync(CompleteCheckoutRequest request, CancellationToken ct)
    {
        try
        {
            await Send.OkAsync(await checkout.CompleteAsync(request, ct), ct);
        }
        catch (Exception ex) when (CheckoutErrors.ToProblem(ex, logger) is { } problem)
        {
            await Send.ResultAsync(problem);
        }
    }
}

public sealed class CompleteCheckoutValidator : Validator<CompleteCheckoutRequest>
{
    public CompleteCheckoutValidator()
    {
        RuleFor(x => x.CorrelationId).NotEmpty().MaximumLength(256);
        RuleFor(x => x.MerchantTransactionId).NotEmpty().MaximumLength(256);
        RuleFor(x => x.TransactionAmount).GreaterThan(0);
        RuleFor(x => x.TransactionCurrencyCode).NotEmpty().Length(3);
    }
}

/// <summary>Maps checkout failures to Problem Details without exposing payload data, keys or paths.</summary>
internal static class CheckoutErrors
{
    public static IResult? ToProblem(Exception ex, ILogger logger)
    {
        switch (ex)
        {
            case MastercardCheckoutException mc:
                logger.LogWarning("Mastercard checkout call failed: {Message}", mc.Message);
                return Results.Problem(statusCode: 502, title: "Mastercard checkout failed.", detail: mc.Message);
            case PayloadDecryptionException:
                return Results.Problem(statusCode: 400, title: "Invalid encrypted payload.");
            case SigningKeyUnavailableException or PayloadKeyUnavailableException:
                return Results.Problem(statusCode: 503, title: "Click to Pay is temporarily unavailable.",
                    detail: ex.Message);
            default:
                return null;
        }
    }
}
