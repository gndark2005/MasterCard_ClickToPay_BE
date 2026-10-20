using FastEndpoints;
using FastEndpoints.Swagger;
using MC_ClickToPay.Api.Authentication;
using MC_ClickToPay.Api.Configuration;
using MC_ClickToPay.Api.Endpoints.Checkout;
using MC_ClickToPay.Api.Keys;
using MC_ClickToPay.Api.Simulation;
using MC_ClickToPay.Services.Abstractions;
using MC_ClickToPay.Services.DependencyInjection;
using MC_ClickToPay.Services.Payments.PowerTranz;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;

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
        settings.Title = "Mastercard Click to Pay - Development API";
        settings.Version = "v1";
        settings.Description = "Decrypts encryptedPayload (JWE) using the Payload Encryption private key. " +
            "Select Authorize and enter the API key from Authentication:ApiKey in appsettings.json, without a Bearer prefix.";
        settings.AddAuth(ApiKeyAuthenticationHandler.SchemeName, new()
        {
            Type = NSwag.OpenApiSecuritySchemeType.ApiKey,
            Name = "X-Api-Key",
            In = NSwag.OpenApiSecurityApiKeyLocation.Header,
            Description = "API key for this development API."
        });
    };
});
builder.Services.AddProblemDetails();
// Binds PayloadEncryption, MastercardApi and Authentication; refuses to start without the decryption settings.
builder.Services.AddRequiredSettings(builder.Configuration);
builder.Services.AddMastercardPayloadDecryption<CertificatePayloadDecryptionKeyProvider>();
// Checkout state (Pending → Confirmed / ConfirmationFailed) in MySQL when ConnectionStrings:ClickToPay is set.
var checkoutDatabase = builder.Configuration.GetConnectionString("ClickToPay");
builder.Services.AddCheckoutDatabase(checkoutDatabase);
if (!string.IsNullOrWhiteSpace(checkoutDatabase))
{
    builder.Services.AddHostedService<CheckoutSchemaInitializer>();
}

builder.Services.AddMastercardCheckout<CertificateSigningKeyProvider>();
if (builder.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("MastercardApi:Simulate"))
{
    // Demos without real checkoutWithCard() ids: only the Mastercard call is simulated.
    builder.Services.AddScoped<IMastercardCheckoutService, SimulatedMastercardCheckoutService>();
}

// Payment flow (decrypt -> validate/map -> IPaymentProcessor), kept registered for the upcoming confirmation
// endpoint: PowerTranz when its credentials are configured (PowerTranz section); otherwise the simulated processor.
var powerTranz = builder.Configuration.GetSection(PowerTranzOptions.SectionName).Get<PowerTranzOptions>();
if (powerTranz?.IsConfigured == true)
{
    builder.Services.AddPowerTranzPaymentProcessor(powerTranz.TimeoutSeconds);
}

builder.Services.AddPaymentConfirmation();
builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();

// Browser callers (e.g. the PowerTranz HPP on staging.ptranz.com): only the configured origins, POST only, and the
// two request headers the API needs. Without Cors:AllowedOrigins no origin is allowed.
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(corsOrigins)
    .WithMethods("POST")
    .WithHeaders("content-type", "x-api-key")
    .WithExposedHeaders("X-Correlation-Id", "X-Src-Cx-Flow-Id")
    .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));

var app = builder.Build();
app.UseExceptionHandler();
app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    await next(context);
});

// The batch confirmation upload is the only request allowed above the 256 KiB default (a daily file has many rows).
app.Use(async (context, next) =>
{
    if (HttpMethods.IsPost(context.Request.Method) && context.Request.HasFormContentType &&
        context.Request.Path.Equals(ConfirmCheckoutEndpoint.Route, StringComparison.OrdinalIgnoreCase) &&
        context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
    {
        limit.MaxRequestBodySize = ConfirmCheckoutEndpoint.MaximumFileUploadBytes;
    }

    await next(context);
});

// Before authentication: the preflight OPTIONS carries no X-Api-Key and is answered here (204) with the CORS headers.
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseFastEndpoints();
app.UseSwaggerGen();
app.Run();

// Exposes the entry point for in-process HTTP integration tests.
public partial class Program;
