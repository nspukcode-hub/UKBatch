using System.Net.Http.Headers;

namespace UKBatch.AspNetCore.Tests.Helpers;

/// <summary>
/// Thin wrapper around <see cref="HttpClient"/> that injects the DevAuth headers
/// (<c>X-Dev-User</c>, <c>X-Dev-Roles</c>) on every request.
/// </summary>
public static class DevAuthClient
{
    /// <summary>
    /// Returns a modified header set ready to use with the underlying <see cref="HttpClient"/>.
    /// Tests can clone the returned dictionary and modify per-call as needed.
    /// </summary>
    public static HttpClient WithDevAuth(this HttpClient client, string user, params string[] roles)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(user);
        client.DefaultRequestHeaders.Remove("X-Dev-User");
        client.DefaultRequestHeaders.Remove("X-Dev-Roles");
        client.DefaultRequestHeaders.Add("X-Dev-User", user);
        if (roles is { Length: > 0 })
        {
            client.DefaultRequestHeaders.Add("X-Dev-Roles", string.Join(',', roles));
        }
        return client;
    }
}
