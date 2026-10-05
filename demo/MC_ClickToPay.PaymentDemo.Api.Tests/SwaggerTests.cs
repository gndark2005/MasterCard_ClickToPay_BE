using System.Text.Json;
using Xunit;

namespace MC_ClickToPay.PaymentDemo.Api.Tests;

public sealed class SwaggerTests
{
    [Fact]
    public async Task PublishesApiKeyProtectedConfirmContract()
    {
        using var factory = new PaymentDemoApiFactory();
        using var client = factory.CreateApiClient(apiKey: null);

        using var document = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));

        var paths = document.RootElement.GetProperty("paths");
        var operation = paths.GetProperty("/api/demo/payments/confirm").GetProperty("post");
        Assert.Contains(operation.GetProperty("security").EnumerateArray(), x => x.TryGetProperty("ApiKey", out _));
        foreach (var status in new[] { "200", "400", "401", "413", "422", "500", "502", "503" })
        {
            Assert.True(operation.GetProperty("responses").TryGetProperty(status, out _), $"Missing {status}.");
        }

        Assert.True(paths.TryGetProperty("/api/demo/payloads/sample", out _));
    }
}
