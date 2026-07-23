using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace UKBatch.Dashboard.Security;

/// <summary>
/// Authentication state provider used when the dashboard runs without an authentication integration.
/// It reports a single authenticated principal so the UI's authorization views render every control,
/// preserving the auth-off default where the whole dashboard is open. It is a UI convenience only and
/// has no bearing on endpoint authorization: a host that gates the dashboard with
/// <c>RequireAuthorization</c> is still enforced at the endpoint before any component renders.
/// </summary>
internal sealed class PermitAllAuthenticationStateProvider : AuthenticationStateProvider
{
    /// <summary>
    /// Authentication type of the auth-off principal. The layout uses it to distinguish the open default
    /// (no real sign-in) from a real authenticated session, so it hides the sign-in pill and sign-out
    /// control when the dashboard is not actually gated.
    /// </summary>
    internal const string AuthDisabledAuthenticationType = "UKBatchAuthDisabled";

    private static readonly Task<AuthenticationState> AuthenticatedState =
        Task.FromResult(BuildState());

    /// <inheritdoc/>
    public override Task<AuthenticationState> GetAuthenticationStateAsync() => AuthenticatedState;

    private static AuthenticationState BuildState()
    {
        // A non-empty AuthenticationType makes IsAuthenticated true, which every authorization view here
        // relies on; the paired always-succeed policies grant the operator/viewer checks.
        var identity = new ClaimsIdentity(AuthDisabledAuthenticationType);
        identity.AddClaim(new Claim(ClaimTypes.Name, "dashboard"));
        return new AuthenticationState(new ClaimsPrincipal(identity));
    }
}
