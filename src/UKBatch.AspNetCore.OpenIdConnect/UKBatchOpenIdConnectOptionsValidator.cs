using Microsoft.Extensions.Options;

namespace UKBatch.AspNetCore.OpenIdConnect;

/// <summary>
/// Validates <see cref="UKBatchOpenIdConnectOptions"/> at host start. A failure throws
/// <see cref="OptionsValidationException"/> so a misconfigured authority, missing client id, or an
/// empty operator-role list fails fast rather than silently granting or denying everyone.
/// </summary>
internal sealed class UKBatchOpenIdConnectOptionsValidator : IValidateOptions<UKBatchOpenIdConnectOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, UKBatchOpenIdConnectOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Authority))
        {
            failures.Add($"{nameof(UKBatchOpenIdConnectOptions.Authority)} must be a non-empty issuer URL.");
        }
        else if (!Uri.TryCreate(options.Authority, UriKind.Absolute, out var authorityUri) ||
                 (authorityUri.Scheme != Uri.UriSchemeHttp && authorityUri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add($"{nameof(UKBatchOpenIdConnectOptions.Authority)} must be an absolute http(s) URL (was '{options.Authority}').");
        }

        if (string.IsNullOrWhiteSpace(options.ClientId))
        {
            failures.Add($"{nameof(UKBatchOpenIdConnectOptions.ClientId)} must be non-empty.");
        }

        // OperatorRoles gate write access. An empty list would deny every user (the operator policy can
        // never be satisfied), so require at least one and reject blank / duplicate entries.
        if (options.OperatorRoles is null || options.OperatorRoles.Count == 0)
        {
            failures.Add($"{nameof(UKBatchOpenIdConnectOptions.OperatorRoles)} must contain at least 1 role name.");
        }
        else
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var role in options.OperatorRoles)
            {
                if (string.IsNullOrWhiteSpace(role))
                {
                    failures.Add($"{nameof(UKBatchOpenIdConnectOptions.OperatorRoles)} contains a null or whitespace entry.");
                    break;
                }
                if (!seen.Add(role))
                {
                    failures.Add($"{nameof(UKBatchOpenIdConnectOptions.OperatorRoles)} contains a duplicate entry '{role}'.");
                    break;
                }
            }
        }

        // RoleClaimPaths may be empty (flattening disabled), but any present entry must be usable.
        if (options.RoleClaimPaths is not null && options.RoleClaimPaths.Count > 0)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in options.RoleClaimPaths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    failures.Add($"{nameof(UKBatchOpenIdConnectOptions.RoleClaimPaths)} contains a null or whitespace entry.");
                    break;
                }
                if (!seen.Add(path))
                {
                    failures.Add($"{nameof(UKBatchOpenIdConnectOptions.RoleClaimPaths)} contains a duplicate entry '{path}'.");
                    break;
                }
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
