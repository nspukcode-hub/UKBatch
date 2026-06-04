using Microsoft.AspNetCore.Authentication;

namespace Sample.Dashboard.DevAuth;

/// <summary>Options for the development-only authentication scheme.</summary>
internal sealed class DevAuthSchemeOptions : AuthenticationSchemeOptions
{
    /// <summary>Scheme name registered with <c>AddAuthentication</c>.</summary>
    public const string SchemeName = "DevAuth";
}
