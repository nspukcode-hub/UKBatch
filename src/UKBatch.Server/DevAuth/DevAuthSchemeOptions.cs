using Microsoft.AspNetCore.Authentication;

namespace UKBatch.Server.DevAuth;

/// <summary>Options for the development-only authentication scheme.</summary>
internal sealed class DevAuthSchemeOptions : AuthenticationSchemeOptions
{
    /// <summary>Scheme name registered with <c>AddAuthentication</c>.</summary>
    public const string SchemeName = "DevAuth";
}
