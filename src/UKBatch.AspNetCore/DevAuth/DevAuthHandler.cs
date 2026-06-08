using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace UKBatch.AspNetCore.DevAuth;

/// <summary>
/// Development-only, header-trusting authentication handler. Reads <c>X-Dev-User</c> as the principal
/// identity and <c>X-Dev-Roles</c> (comma-separated) as role claims. The caller self-asserts identity:
/// there is NO password, token, or signature verification, so this is for local development and demos
/// only — never production.
/// </summary>
/// <remarks>
/// Emits only <see cref="ClaimTypes.Role"/> role claims (no <c>"role"</c> mirror);
/// <c>[Authorize(Roles=...)]</c> reads <see cref="ClaimTypes.Role"/> directly.
/// </remarks>
internal sealed class DevAuthHandler : AuthenticationHandler<DevAuthSchemeOptions>
{
    /// <summary>Constructs the handler. Standard ASP.NET Core authentication-handler signature.</summary>
    public DevAuthHandler(
        IOptionsMonitor<DevAuthSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    /// <inheritdoc/>
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
