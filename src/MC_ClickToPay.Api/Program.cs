using FastEndpoints;
using FastEndpoints.Swagger;
using MC_ClickToPay.Api.Authentication;
using MC_ClickToPay.Api.Keys;
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
builder.Services.Configure<PayloadEncryptionOptions>(builder.Configuration.GetSection("PayloadEncryption"));
builder.Services.AddMastercardPayloadDecryption<CertificatePayloadDecryptionKeyProvider>();
builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationHandler.SchemeName, _ => { });
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
app.UseFastEndpoints();
app.UseSwaggerGen();
app.Run();

// Exposes the entry point for in-process HTTP integration tests.
public partial class Program;
