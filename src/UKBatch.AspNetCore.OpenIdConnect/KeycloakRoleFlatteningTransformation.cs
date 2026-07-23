using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace UKBatch.AspNetCore.OpenIdConnect;

/// <summary>
/// Flattens an identity provider's nested JSON role arrays into standard
/// <see cref="ClaimTypes.Role"/> claims so a single role-claim path drives both the operator policy and
/// the approval-gate role check. Runs on every <c>AuthenticateAsync</c> (cookie and bearer principals
/// alike), which is why it must be idempotent.
/// </summary>
/// <remarks>
/// The configured <see cref="UKBatchOpenIdConnectOptions.RoleClaimPaths"/> select the source claims; the
/// defaults match Keycloak's <c>realm_access.roles</c> and <c>resource_access.*.roles</c> shapes (the
/// <c>*</c> matches every client). A missing claim or malformed JSON is skipped, never thrown.
/// </remarks>
internal sealed class KeycloakRoleFlatteningTransformation : IClaimsTransformation
{
    /// <summary>
    /// Marks a principal whose roles have already been flattened, so a second pass within the same
    /// request is a no-op. It is a bookkeeping marker only and is never consulted for authorization —
    /// authorization reads the <see cref="ClaimTypes.Role"/> claims, which come from the
    /// signature-validated token.
    /// </summary>
    internal const string FlattenedSentinelClaimType = "ukbatch:roles-flattened";

    private readonly IOptionsMonitor<UKBatchOpenIdConnectOptions> _options;

    public KeycloakRoleFlatteningTransformation(IOptionsMonitor<UKBatchOpenIdConnectOptions> options)
    {
        _options = options;
    }

    /// <inheritdoc/>
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        // Append to the primary identity in place. A `new ClaimsIdentity(principal.Claims)` clone would
        // drop the AuthenticationType, flipping IsAuthenticated to false and breaking both
        // RequireAuthenticatedUser() and the approver-identity harvest.
        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
        {
            return Task.FromResult(principal);
        }

        if (principal.HasClaim(static c => c.Type == FlattenedSentinelClaimType))
        {
            return Task.FromResult(principal);
        }

        identity.AddClaim(new Claim(FlattenedSentinelClaimType, "true"));

        var paths = _options.CurrentValue.RoleClaimPaths;
        if (paths is { Count: > 0 })
        {
            foreach (var path in paths)
            {
                FlattenPath(identity, path);
            }
        }

        return Task.FromResult(principal);
    }

    private static void FlattenPath(ClaimsIdentity identity, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var segments = path.Split('.');
        if (segments.Length < 2)
        {
            // Nothing to navigate into (need at least a source claim + the array property).
            return;
        }

        var sourceClaim = identity.FindFirst(segments[0]);
        if (sourceClaim is null)
        {
            return;
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(sourceClaim.Value);
            // Clone so the element outlives the disposed document for the recursive walk below.
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Malformed source claim — skip this path rather than fail the whole authentication.
            return;
        }

        CollectRoles(root, segments, index: 1, identity);
    }

    private static void CollectRoles(JsonElement element, string[] segments, int index, ClaimsIdentity identity)
    {
        // The last segment names the role array; earlier segments navigate objects, with `*` fanning
        // out over every property (e.g. every client under resource_access).
        if (index == segments.Length - 1)
        {
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(segments[index], out var roles) &&
                roles.ValueKind == JsonValueKind.Array)
            {
                foreach (var role in roles.EnumerateArray())
                {
                    if (role.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var value = role.GetString();
                    if (!string.IsNullOrEmpty(value) && !identity.HasClaim(ClaimTypes.Role, value))
                    {
                        identity.AddClaim(new Claim(ClaimTypes.Role, value));
                    }
                }
            }

            return;
        }

        var segment = segments[index];
        if (segment == "*")
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    CollectRoles(property.Value, segments, index + 1, identity);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Object &&
                 element.TryGetProperty(segment, out var child))
        {
            CollectRoles(child, segments, index + 1, identity);
        }
    }
}
