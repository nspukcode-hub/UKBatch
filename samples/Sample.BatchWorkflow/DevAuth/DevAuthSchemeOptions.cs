using Microsoft.AspNetCore.Authentication;

namespace Sample.BatchWorkflow.DevAuth;

/// <summary>
/// Options for the development-only authentication scheme. DEVELOPMENT ONLY — not for production.
/// </summary>
internal sealed class DevAuthSchemeOptions : AuthenticationSchemeOptions
{
    /// <summary>Scheme name registered with <c>AddAuthentication</c>.</summary>
    public const string SchemeName = "DevAuth";
}
