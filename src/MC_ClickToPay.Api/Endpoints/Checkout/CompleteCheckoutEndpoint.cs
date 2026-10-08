using FastEndpoints;
using FluentValidation;
using MC_ClickToPay.Api.Keys;
using MC_ClickToPay.Services.Abstractions;
using MC_ClickToPay.Services.Checkout;
using MC_ClickToPay.Services.Exceptions;
using Microsoft.Extensions.Options;

namespace MC_ClickToPay.Api.Endpoints.Checkout;

public sealed class CompleteCheckoutEndpoint(IMastercardCheckoutService checkout, ILogger<CompleteCheckoutEndpoint> logger)
    : Endpoint<CompleteCheckoutRequest, CompleteCheckoutResponse>
{
    public override void Configure()
    {
        Post("/api/checkout");
        Summary(s =>
        {
            s.Summary = "Complete a Click to Pay checkout";
            s.Description = "Takes the srcCorrelationId, merchant-transaction-id and x-src-cx-flow-id returned by " +
                "checkoutWithCard(), calls Mastercard POST /srci/api/checkout (OAuth 1.0a) and decrypts the " +
                "encryptedPayload. Returns the decrypted payload always with a card object: the PAN when Mastercard " +
                "sent one (credentialType Pan), otherwise the network token in card.primaryAccountNumber " +
                "(credentialType NetworkToken). The token, cryptogram/DTVC, addresses and the ECI are included. " +
                "No PowerTranz call is made: the UI sends the card to PowerTranz SPI itself. srcDpaId must match " +
                "the configured DPA.";
            s.ExampleRequest = new CompleteCheckoutRequest
            {
                SpiToken = "<PowerTranz SpiToken>",
                SrcDpaId = "823ef281-1a2e-4204-a69c-43d355522a35",
                SrcCorrelationId = "34f4a04b.2ce61515-55b7-4c09-845b-45492c2ef327",
                MerchantTransactionId = "0a4e0d3.34f4a04b.f91587770b3c99ce1090fe8a4f2a931b10d7acfc",
                FlowId = "34f4a04b.2ce61515-55b7-4c09-845b-45492c2ef327.1790888627",
                XCorrelationId = "7d1f6c2e-9a43-4b8e-b1a2-3f5c8e0d4a17"
            };
            s.Response<CompleteCheckoutResponse>(200, "Decrypted payload with card, credentialType and ECI.");
            s.Response(400, "Invalid request or invalid encrypted payload.");
            s.Response(401, "API key is missing, incorrect or not configured.");
            s.Response(502, "Mastercard rejected the call or could not be reached.");
            s.Response(503, "Signing key or decryption certificate is not configured or unavailable.");
        });
    }

    public override async Task HandleAsync(CompleteCheckoutRequest request, CancellationToken ct)
    {
        HttpContext.Response.Headers["X-Correlation-Id"] = request.XCorrelationId;
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["XCorrelationId"] = request.XCorrelationId });
        try
        {
            var result = await checkout.CompleteAsync(request, ct);
            var response = CompleteCheckoutResponse.From(result);

            // Format only: never card numbers, cryptograms or security codes.
            logger.LogInformation(
                "Checkout {CorrelationId} (x-correlation-id {XCorrelationId}): decrypted {PayloadModel} (credentialType {CredentialType}, dynamicDataType {DynamicDataType}, eci {Eci}).",
                request.SrcCorrelationId, request.XCorrelationId, result.Payload.GetType().Name, response.CredentialType,
                response.DynamicDataType ?? "-", response.Eci ?? "-");
            await Send.OkAsync(response, ct);
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
        RuleFor(x => x.SpiToken).NotEmpty().MaximumLength(2048);
        RuleFor(x => x.SrcDpaId).NotEmpty().MaximumLength(256)
            .Must(id => string.Equals(id, Resolve<IOptions<MastercardCheckoutOptions>>().Value.SrcDpaId, StringComparison.Ordinal))
            .WithMessage("srcDpaId does not match the configured DPA.");
        RuleFor(x => x.SrcCorrelationId).NotEmpty().MaximumLength(256);
        RuleFor(x => x.MerchantTransactionId).NotEmpty().MaximumLength(256);
        RuleFor(x => x.FlowId).NotEmpty().MaximumLength(256);
        // Echoed in a response header: visible ASCII only.
        RuleFor(x => x.XCorrelationId).NotEmpty().MaximumLength(256).Matches("^[\\x21-\\x7E]+$")
            .WithMessage("xCorrelationId must be visible ASCII characters without spaces.");
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
