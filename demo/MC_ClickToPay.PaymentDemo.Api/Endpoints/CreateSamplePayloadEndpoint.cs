using System.Text.Json;
using System.Text.Json.Serialization;
using FastEndpoints;
using Jose;
using MC_ClickToPay.PaymentDemo.Api.Configuration;
using MC_ClickToPay.PaymentDemo.Api.Keys;
using Microsoft.Extensions.Options;

namespace MC_ClickToPay.PaymentDemo.Api.Endpoints;

/// <summary>
/// Development helper: encrypts PaymentDemo:TestPayment (plus any overrides in the body) exactly as Mastercard
/// encrypts a Click to Pay payload, so the confirm endpoint can be tested without a real Mastercard checkout.
/// </summary>
public sealed class CreateSamplePayloadEndpoint(
    IOptionsSnapshot<PaymentDemoOptions> options, IOptions<DemoPayloadEncryptionOptions> encryption,
    DemoPayloadKeyProvider keys)
    : Endpoint<TestPaymentData, SamplePayloadResponse>
{
    public const string Route = "/api/demo/payloads/sample";

    private static readonly JsonSerializerOptions PayloadJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public override void Configure()
    {
        Post(Route);
        Summary(s =>
        {
            s.Summary = "Create an encrypted test payload (development only)";
            s.Description = "Encrypts the test card/payment data from PaymentDemo:TestPayment as a compact JWE " +
                "(RSA-OAEP-256 / A128CBC-HS256) with the configured key. Send {} to use the configuration as is; " +
                "any non-null field in the body overrides it (e.g. an expired tokenExpirationYear). " +
                "Disabled unless PaymentDemo:EnableSamplePayloadEndpoint is true.";
            s.ExampleRequest = new TestPaymentData();
            s.Response<SamplePayloadResponse>(200, "encryptedPayload to send to " + ConfirmPaymentEndpoint.Route + ".");
            s.Response(401, "API key is missing, incorrect or not configured.");
            s.Response(404, "sample_payloads_disabled.");
            s.Response(503, "decryption_unavailable: no key/certificate is configured or usable.");
        });
    }

    public override async Task HandleAsync(TestPaymentData request, CancellationToken ct)
    {
        var settings = options.Value;
        if (!settings.EnableSamplePayloadEndpoint)
        {
            await Send.ResultAsync(DemoProblems.Create(404, DemoProblems.SamplePayloadsDisabled,
                "Sample payloads are disabled.",
                "Set PaymentDemo:EnableSamplePayloadEndpoint to true (local development only)."));
            return;
        }

        var json = JsonSerializer.Serialize(settings.TestPayment.ToDecryptedPayload(request), PayloadJson);
        try
        {
            using var key = keys.GetPublicKey();
            var encrypted = JWT.Encode(json, key, JweAlgorithm.RSA_OAEP_256, JweEncryption.A128CBC_HS256);
            await Send.OkAsync(new SamplePayloadResponse
            {
                EncryptedPayload = encrypted,
                Note = string.IsNullOrWhiteSpace(encryption.Value.CertificatePath)
                    ? "Encrypted with the ephemeral development key: valid until the API restarts."
                    : "Encrypted with the public key of the configured PayloadEncryption certificate."
            }, ct);
        }
        catch (DemoKeyUnavailableException)
        {
            await Send.ResultAsync(DemoProblems.Create(503, DemoProblems.DecryptionUnavailable,
                "Payload encryption key is unavailable."));
        }
    }
}
