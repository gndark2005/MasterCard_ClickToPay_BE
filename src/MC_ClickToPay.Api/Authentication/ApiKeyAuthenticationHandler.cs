using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace MC_ClickToPay.Api.Authentication;

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var expected = configuration["Authentication:ApiKey"];
        if (string.IsNullOrWhiteSpace(expected) ||
            !Request.Headers.TryGetValue("X-Api-Key", out var supplied) || supplied.Count != 1 ||
            !string.Equals(expected, supplied.ToString(), StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API credentials."));
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "internal-api-client")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
