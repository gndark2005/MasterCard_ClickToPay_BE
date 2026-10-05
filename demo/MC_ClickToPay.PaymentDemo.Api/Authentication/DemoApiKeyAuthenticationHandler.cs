using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace MC_ClickToPay.PaymentDemo.Api.Authentication;

/// <summary>Same X-Api-Key scheme as MC_ClickToPay.Api, kept in the demo so neither project depends on the other.</summary>
public sealed class DemoApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";

    public const string HeaderName = "X-Api-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var expected = configuration["Authentication:ApiKey"];
        if (string.IsNullOrWhiteSpace(expected) ||
            !Request.Headers.TryGetValue(HeaderName, out var supplied) || supplied.Count != 1 ||
            !string.Equals(expected, supplied.ToString(), StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API credentials."));
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "payment-demo-client")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
