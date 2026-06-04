using Microsoft.Extensions.Options;

namespace UKBatch.Api.Common;

/// <summary>
/// Resolves effective offset/limit values from query params against <see cref="UKBatchOptions"/>
/// (<c>DefaultPageLimit</c> + <c>MaxPageLimit</c>). Internal helper consumed by all paginated
/// REST endpoints.
/// </summary>
internal static class PaginationDefaults
{
    /// <summary>
    /// Validates and normalizes <paramref name="offset"/> + <paramref name="limit"/> against the
    /// options snapshot. Returns <c>true</c> on success; on failure populates
    /// <paramref name="errors"/> with a field/message map suitable for <c>Results.ValidationProblem</c>.
    /// </summary>
    public static bool TryValidate(
        IOptions<UKBatchOptions> options,
        int? offset,
        int? limit,
        out int effectiveOffset,
        out int effectiveLimit,
        out IDictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(options);
        var opts = options.Value;
        errors = new Dictionary<string, string[]>();
        effectiveOffset = offset ?? 0;
        effectiveLimit = limit ?? opts.DefaultPageLimit;

        if (effectiveOffset < 0)
        {
            errors["offset"] = ["offset must be >= 0."];
        }
        if (effectiveLimit < 1)
        {
            errors["limit"] = ["limit must be >= 1."];
        }
        else if (effectiveLimit > opts.MaxPageLimit)
        {
            errors["limit"] = [$"limit must be <= MaxPageLimit ({opts.MaxPageLimit})."];
        }
        return errors.Count == 0;
    }
}
