using Microsoft.AspNetCore.Authentication;

namespace UKBatch.AspNetCore.DevAuth;

/// <summary>
/// Options for the development-only authentication scheme registered by
/// <see cref="DevAuthServiceCollectionExtensions.AddUKBatchDevAuth"/>. Development / demo only —
/// not for production.
/// </summary>
internal sealed class DevAuthSchemeOptions : AuthenticationSchemeOptions
{
    /// <summary>Scheme name registered with <c>AddAuthentication</c>.</summary>
    public const string SchemeName = "DevAuth";
}
