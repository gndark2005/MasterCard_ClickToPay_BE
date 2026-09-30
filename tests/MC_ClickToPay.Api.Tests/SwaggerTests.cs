using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace MC_ClickToPay.Api.Tests;

public sealed class SwaggerTests
{
    [Fact]
    public async Task PublishesSwaggerUiAndApiKeyProtectedContract()
    {
        using var factory = new PayloadApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        var html = await client.GetStringAsync("/swagger/index.html");
        Assert.Contains("swagger-ui", html);
        using var document = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
        var root = document.RootElement;
        var scheme = root.GetProperty("components").GetProperty("securitySchemes").GetProperty("ApiKey");
        Assert.Equal("apiKey", scheme.GetProperty("type").GetString());
        Assert.Equal("X-Api-Key", scheme.GetProperty("name").GetString());
        Assert.Equal("header", scheme.GetProperty("in").GetString());
        var operation = root.GetProperty("paths").GetProperty("/api/payloads/decrypt").GetProperty("post");
        Assert.Contains(operation.GetProperty("security").EnumerateArray(), x => x.TryGetProperty("ApiKey", out _));
        foreach (var status in new[] { "200", "400", "401", "413", "503" })
        {
            Assert.True(operation.GetProperty("responses").TryGetProperty(status, out _));
        }

        Assert.True(operation.TryGetProperty("requestBody", out _));
    }
}
