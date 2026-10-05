using FastEndpoints;
using FastEndpoints.Swagger;
using MC_ClickToPay.PaymentDemo.Api.Authentication;
using MC_ClickToPay.PaymentDemo.Api.Configuration;
using MC_ClickToPay.PaymentDemo.Api.Keys;
using MC_ClickToPay.Services.DependencyInjection;
using MC_ClickToPay.Services.Payments;
using Microsoft.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);
if (builder.Environment.IsDevelopment())
{
    // Git-ignored local settings (key paths, passwords); they override appsettings.json and User Secrets.
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
}

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

// Decryption: the existing MC_ClickToPay.Services implementation with the demo's own key provider.
builder.Services.Configure<DemoPayloadEncryptionOptions>(builder.Configuration.GetSection(DemoPayloadEncryptionOptions.SectionName));
builder.Services.AddSingleton<EphemeralDevelopmentKey>();
builder.Services.AddScoped<DemoPayloadKeyProvider>();
builder.Services.AddMastercardPayloadDecryption<DemoPayloadKeyProvider>();

// Payment: the shared MC_ClickToPay.Services flow (decrypted payload -> PaymentRequest -> IPaymentProcessor), the same
// one MC_ClickToPay.Api uses. The simulated processor stays until PowerTranz is plugged in (see README).
builder.Services.Configure<PaymentDemoOptions>(builder.Configuration.GetSection(PaymentDemoOptions.SectionName));
builder.Services.Configure<PaymentSimulationOptions>(
    builder.Configuration.GetSection($"{PaymentDemoOptions.SectionName}:Simulation"));
builder.Services.AddPaymentConfirmation();

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
