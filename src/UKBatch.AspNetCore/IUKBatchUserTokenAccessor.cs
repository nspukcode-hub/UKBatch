namespace UKBatch.AspNetCore;

/// <summary>
/// Supplies the current user's bearer access token for outbound calls the dashboard makes to a
/// UKBatch API on the user's behalf. An authentication integration (for example the OpenID Connect
/// package) registers an implementation; when none is registered the dashboard falls back to its
/// static service-descriptor headers (the machine-identity path).
/// </summary>
public interface IUKBatchUserTokenAccessor
{
    /// <summary>
    /// Returns the current user's access token, or <c>null</c> when it is unavailable (no signed-in
    /// session, or the token could not be obtained). Implementations may refresh a near-expired token
    /// before returning it.
    /// </summary>
    /// <param name="cancellationToken">Cancels the lookup (and any refresh) in flight.</param>
    ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken);
}
