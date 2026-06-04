using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Sample.Dashboard.DevAuth;

/// <summary>
/// DEVELOPMENT ONLY header-based authentication handler. Reads <c>X-Dev-User</c> as the
/// principal identity and <c>X-Dev-Roles</c> (comma-separated) as role claims.
/// </summary>
/// <remarks>
/// Mirror of <c>Sample.RestApi.DevAuth.DevAuthHandler</c>: same DevAuth surface used by approval
/// endpoints, plus integration-test wiring (auth-on smoke).
/// </remarks>
internal sealed class DevAuthHandler : AuthenticationHandler<DevAuthSchemeOptions>
{
    public DevAuthHandler(
        IOptionsMonitor<DevAuthSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Dev-User", out var user) || string.IsNullOrEmpty(user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }
        var claims = new List<Claim> { new(ClaimTypes.Name, user!) };
        if (Request.Headers.TryGetValue("X-Dev-Roles", out var roles))
        {
            foreach (var r in roles.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                claims.Add(new Claim(ClaimTypes.Role, r));
            }
        }
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
