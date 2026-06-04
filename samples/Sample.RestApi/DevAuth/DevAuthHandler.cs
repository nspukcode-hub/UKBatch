using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Sample.RestApi.DevAuth;

/// <summary>
/// DEVELOPMENT ONLY header-based authentication handler. Reads <c>X-Dev-User</c> as the
/// principal identity and <c>X-Dev-Roles</c> (comma-separated) as role claims.
/// </summary>
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
        // Test helper: optional `X-Dev-Custom-Role-Type` + `X-Dev-Custom-Roles`
        // headers emit role claims under a CUSTOM claim type (e.g. "role" for IdentityServer-style).
        // Production never sets these — they exist for ApprovalRoleClaimTypes endpoint tests.
        if (Request.Headers.TryGetValue("X-Dev-Custom-Role-Type", out var customType)
            && !string.IsNullOrEmpty(customType.ToString())
            && Request.Headers.TryGetValue("X-Dev-Custom-Roles", out var customRoles))
        {
            foreach (var r in customRoles.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                claims.Add(new Claim(customType.ToString(), r));
            }
        }
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
