using System.Text.Json;
using MC_ClickToPay.Services.Abstractions;
using MC_ClickToPay.Services.Checkout;
using MC_ClickToPay.Services.Models;

namespace MC_ClickToPay.Api.Simulation;

/// <summary>
/// Development only (MastercardApi:Simulate = true): replaces the Mastercard /checkout call with a fixed sandbox
/// DSRP + PAN payload, so POST /api/checkout (validation, response and the daily transaction file) can be demonstrated
/// without real checkoutWithCard() ids. Never registered outside the Development environment.
/// </summary>
public sealed class SimulatedMastercardCheckoutService(ILogger<SimulatedMastercardCheckoutService> logger)
    : IMastercardCheckoutService
{
    // Mastercard sandbox test card shared by the team; not a real card.
    private const string SandboxPayload = """
        {"card":{"primaryAccountNumber":"5120350100064537","panExpirationMonth":"07","panExpirationYear":"2029","cardholderFullName":"John Doe"},
         "dynamicData":{"dynamicDataValue":"AH14E2rQmy6mABQkMkPpAAADFA==","dynamicDataType":"CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM"},
         "billingAddress":{"line1":"150 5th Avenue","city":"New York","state":"NY","countryCode":"US","zip":"10011"},
         "consumerEmailAddress":"john.doe@example.com",
         "consumerMobileNumber":{"countryCode":"1","phoneNumber":"2125550100"}}
        """;

    public Task<CompleteCheckoutResult> CompleteAsync(CompleteCheckoutRequest request, CancellationToken cancellationToken = default)
    {
        logger.LogWarning("SIMULATED Mastercard /checkout for {CorrelationId}: no call was made to Mastercard.", request.SrcCorrelationId);
        return Task.FromResult(new CompleteCheckoutResult
        {
            MerchantTransactionId = request.MerchantTransactionId,
            CorrelationId = request.SrcCorrelationId,
            Eci = "02",
            Payload = JsonSerializer.Deserialize<DecryptedPayloadDto>(SandboxPayload)!
        });
    }

    public Task<CheckoutConfirmationResult> ConfirmAsync(CheckoutConfirmationRequest request, CancellationToken cancellationToken = default)
    {
        logger.LogWarning("SIMULATED Mastercard /checkout/confirmations for {CorrelationId}: no call was made to Mastercard.",
            request.CorrelationId);
        return Task.FromResult(new CheckoutConfirmationResult { FlowId = request.FlowId });
    }
}
