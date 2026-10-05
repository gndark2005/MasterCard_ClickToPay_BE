using System.Net.Http.Json;
using System.Text.Json;
using Jose;
using MC_ClickToPay.PaymentDemo.Api.Keys;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MC_ClickToPay.PaymentDemo.Api.Tests;

public sealed class PaymentDemoApiFactory : WebApplicationFactory<Program>
{
    public const string ApiKey = "integration-test-only-api-key";

    /// <summary>The configured test token and cryptogram (appsettings.json), which must never appear in responses or logs.</summary>
    public const string ConfiguredToken = "5480983179133165";

    public const string ConfiguredCryptogram = "DEMOcryptogramNOTREAL000000=";

    /// <summary>Configuration overrides; change before the first request.</summary>
    public Dictionary<string, string?> Settings { get; } = new()
    {
        ["Authentication:ApiKey"] = ApiKey,
        ["PayloadEncryption:UseEphemeralDevelopmentKey"] = "true",
        ["PaymentDemo:EnableSamplePayloadEndpoint"] = "true"
    };

    public Action<IServiceCollection>? TestServices { get; set; }

    public CapturingLoggerProvider Logs { get; } = new();

    public HttpClient CreateApiClient(string? apiKey = ApiKey)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        }

        return client;
    }

    /// <summary>Encrypts arbitrary JSON with the API's own public key, like Mastercard would.</summary>
    public string Encrypt(string json)
    {
        using var scope = Services.CreateScope();
        using var key = scope.ServiceProvider.GetRequiredService<DemoPayloadKeyProvider>().GetPublicKey();
        return JWT.Encode(json, key, JweAlgorithm.RSA_OAEP_256, JweEncryption.A128CBC_HS256);
    }

    /// <summary>Calls the sample endpoint; <paramref name="overrides"/> replaces configured test payment fields.</summary>
    public static async Task<string> CreateSamplePayloadAsync(HttpClient client, object? overrides = null)
    {
        using var response = await client.PostAsJsonAsync("/api/demo/payloads/sample", overrides ?? new { });
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("encryptedPayload").GetString()!;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(Settings));
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureTestServices(services => TestServices?.Invoke(services));
    }
}
