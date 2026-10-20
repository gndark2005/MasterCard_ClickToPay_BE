using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace MC_ClickToPay.Api.Tests;

/// <summary>The PowerTranz HPP calls the API from the browser (origin https://staging.ptranz.com).</summary>
public sealed class CorsTests
{
    private const string HppOrigin = "https://staging.ptranz.com";

    [Theory]
    [InlineData("/api/checkout")]
    [InlineData("/api/confirmations/spi-token-1")]
    public async Task PreflightFromTheHppIsAnsweredWithoutAnApiKey(string route)
    {
        using var factory = new PayloadApiFactory();
        using var client = Client(factory);
        using var request = new HttpRequestMessage(HttpMethod.Options, route);
        request.Headers.Add("Origin", HppOrigin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type,x-api-key");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HppOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("POST", response.Headers.GetValues("Access-Control-Allow-Methods").Single());
        var allowedHeaders = response.Headers.GetValues("Access-Control-Allow-Headers").Single().ToLowerInvariant();
        Assert.Contains("content-type", allowedHeaders);
        Assert.Contains("x-api-key", allowedHeaders);
    }

    [Fact]
    public async Task PostFromTheHppCarriesTheAllowOriginHeader()
    {
        using var factory = new PayloadApiFactory();
        using var client = Client(factory);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/checkout")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Origin", HppOrigin);
        request.Headers.Add("X-Api-Key", PayloadApiFactory.ApiKey);

        using var response = await client.SendAsync(request);

        // Validation error, but the browser can read it.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HppOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task OtherOriginsAreNotAllowed()
    {
        using var factory = new PayloadApiFactory();
        using var client = Client(factory);
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/checkout");
        request.Headers.Add("Origin", "https://evil.example.com");
        request.Headers.Add("Access-Control-Request-Method", "POST");

        using var response = await client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
}
