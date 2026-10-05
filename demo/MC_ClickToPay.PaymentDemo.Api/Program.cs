using FastEndpoints;
using FastEndpoints.Swagger;
using MC_ClickToPay.PaymentDemo.Api.Authentication;
using MC_ClickToPay.PaymentDemo.Api.Configuration;
using MC_ClickToPay.PaymentDemo.Api.Keys;
using MC_ClickToPay.PaymentDemo.Api.Payments;
using MC_ClickToPay.Services.DependencyInjection;
using Microsoft.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 256 * 1024);
builder.Services.AddFastEndpoints();
builder.Services.SwaggerDocument(options =>
{
    options.EnableJWTBearerAuth = false;
    options.DocumentSettings = settings =>
    {
        settings.DocumentName = "v1";
        settings.Title = "Mastercard Click to Pay - Payment Confirmation Demo API";
        settings.Version = "v1";
        settings.Description = "Demo only: decrypts a Click to Pay encryptedPayload and returns a SIMULATED payment " +
            "confirmation. No real PowerTranz payment is made. Select Authorize and enter the API key from " +
            "Authentication:ApiKey, without a Bearer prefix.";
        settings.AddAuth(DemoApiKeyAuthenticationHandler.SchemeName, new()
        {
            Type = NSwag.OpenApiSecuritySchemeType.ApiKey,
            Name = DemoApiKeyAuthenticationHandler.HeaderName,
            In = NSwag.OpenApiSecurityApiKeyLocation.Header,
            Description = "API key for this demo API."
        });
    };
});
builder.Services.AddProblemDetails();
builder.Services.AddSingleton(TimeProvider.System);

// Decryption: the existing MC_ClickToPay.Services implementation with the demo's own key provider.
builder.Services.Configure<DemoPayloadEncryptionOptions>(builder.Configuration.GetSection(DemoPayloadEncryptionOptions.SectionName));
builder.Services.AddSingleton<EphemeralDevelopmentKey>();
builder.Services.AddScoped<DemoPayloadKeyProvider>();
builder.Services.AddMastercardPayloadDecryption<DemoPayloadKeyProvider>();

// Payment: decrypted payload -> PaymentRequest -> IPaymentProcessor. Replace SimulatedPaymentProcessor with the
// PowerTranz implementation here once a test card can complete the Mastercard -> PowerTranz flow (see README).
builder.Services.Configure<PaymentDemoOptions>(builder.Configuration.GetSection(PaymentDemoOptions.SectionName));
builder.Services.AddSingleton<PaymentRequestFactory>();
builder.Services.AddScoped<IPaymentProcessor, SimulatedPaymentProcessor>();
builder.Services.AddScoped<PaymentConfirmationService>();

builder.Services.AddAuthentication(DemoApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, DemoApiKeyAuthenticationHandler>(
        DemoApiKeyAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();

var app = builder.Build();
app.UseExceptionHandler();
app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
app.UseAuthentication();
app.UseAuthorization();
app.UseFastEndpoints(options => options.Errors.UseProblemDetails());
app.UseSwaggerGen();
app.Run();

// Exposes the entry point for in-process HTTP integration tests.
public partial class Program;
