using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace UKBatch.Server.DevAuth;

/// <summary>
/// DEVELOPMENT ONLY header-based authentication handler. Reads <c>X-Dev-User</c> as the
/// principal identity and <c>X-Dev-Roles</c> (comma-separated) as role claims.
/// </summary>
/// <remarks>
/// Sample-local copy of <c>Sample.Dashboard.DevAuth.DevAuthHandler</c> (NOT NuGet-shipped — Server is
/// the Docker image). Opt-in only: registered behind the <c>UKBATCH_DEV_AUTH</c> flag (default false),
/// so the demo's approval gate (<c>allowedRoles:["ops"]</c>) can be granted via curl with the role
/// header. The browser dashboard approve button cannot inject the header → curl is the approval path;
/// full OIDC/Cookie login is a v0.2 concern.
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
