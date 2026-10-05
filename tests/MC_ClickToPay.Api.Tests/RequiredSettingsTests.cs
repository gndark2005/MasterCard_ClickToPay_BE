using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MC_ClickToPay.Api.Tests;

public sealed class RequiredSettingsTests
{
    [Fact]
    public void RefusesToStartWithoutDecryptionSettingsAndNamesThem()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Authentication:ApiKey"] = "",
                    ["PayloadEncryption:CertificatePath"] = "",
                }));
        });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient()).ToString();

        Assert.Contains("Authentication:ApiKey", error);
        Assert.Contains("PayloadEncryption:CertificatePath", error);
        Assert.DoesNotContain("MastercardApi", error);
    }
}
