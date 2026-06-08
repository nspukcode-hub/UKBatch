namespace UKBatch.AspNetCore.DevAuth;

/// <summary>
/// Options for <see cref="DevAuthServiceCollectionExtensions.AddUKBatchDevAuth"/>, the development-only
/// header-trusting authentication helper.
/// </summary>
/// <remarks>
/// The dev-auth scheme trusts the <c>X-Dev-User</c> / <c>X-Dev-Roles</c> request headers verbatim with
/// NO verification, so callers can self-assert any identity. It exists to make the dashboard approval
/// buttons and role-gated endpoints work in local demos. It is refused at startup in the Production
/// environment unless <see cref="AllowInProduction"/> is explicitly set. Use OIDC (or another real
/// authentication scheme) in production.
/// </remarks>
public sealed class UKBatchDevAuthOptions
{
    /// <summary>
    /// When <see langword="false"/> (the default) the dev-auth startup guard throws in the Production
    /// environment, failing the host start rather than silently trusting unverified headers in
    /// production. Set to <see langword="true"/> only to deliberately override that fail-closed default
    /// (for example, a throwaway demo deployment that is intentionally marked Production); doing so is
    /// insecure and never appropriate for a real production system.
    /// </summary>
    public bool AllowInProduction { get; set; }
}
