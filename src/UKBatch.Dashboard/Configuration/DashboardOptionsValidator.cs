using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace UKBatch.Dashboard.Configuration;

/// <summary>
/// Validates <see cref="DashboardOptions"/> at host startup. Fail-fast: <c>IsValid = false</c>
/// throws <see cref="OptionsValidationException"/> via the host pipeline before any request
/// is served.
/// </summary>
internal sealed partial class DashboardOptionsValidator : IValidateOptions<DashboardOptions>
{
    [GeneratedRegex(@"^[a-z][a-z0-9-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex KebabCase();

    public ValidateOptionsResult Validate(string? name, DashboardOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();

        // No BasePath validation — there is no BasePath option. Routes are pinned literal.

        // Services list
        if (options.Services is null || options.Services.Count == 0)
        {
            errors.Add("Dashboard.Services must contain at least one service descriptor.");
        }
        else
        {
            var seenNames = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < options.Services.Count; i++)
            {
                var s = options.Services[i];
                if (s is null)
                {
                    errors.Add($"Dashboard.Services[{i}] is null.");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(s.Name) || !KebabCase().IsMatch(s.Name))
                {
                    errors.Add($"Dashboard.Services[{i}].Name must be kebab-case (^[a-z][a-z0-9-]*$); got '{s.Name}'.");
                }
                else if (!seenNames.Add(s.Name))
                {
                    errors.Add($"Dashboard.Services[{i}].Name '{s.Name}' is duplicated.");
                }
                if (s.BaseUrl is null || !s.BaseUrl.IsAbsoluteUri)
                {
                    errors.Add($"Dashboard.Services[{i}].BaseUrl must be an absolute URI; got '{s.BaseUrl}'.");
                }
                if (string.IsNullOrWhiteSpace(s.HubPath) || !s.HubPath.StartsWith('/'))
                {
                    errors.Add($"Dashboard.Services[{i}].HubPath must start with '/' (got '{s.HubPath}').");
                }
            }
        }

        // Numeric / temporal bounds
        if (options.DefaultPageSize < 1)
            errors.Add($"Dashboard.DefaultPageSize must be >= 1 (got {options.DefaultPageSize}).");
        if (options.HttpTimeout <= TimeSpan.Zero)
            errors.Add($"Dashboard.HttpTimeout must be > 0 (got {options.HttpTimeout}).");
        if (options.DedupeCacheCapacity < 1)
            errors.Add($"Dashboard.DedupeCacheCapacity must be >= 1 (got {options.DedupeCacheCapacity}).");
        if (options.ReconnectDelays is { } delays)
        {
            for (var i = 0; i < delays.Count; i++)
            {
                if (delays[i] < TimeSpan.Zero)
                {
                    errors.Add($"Dashboard.ReconnectDelays[{i}] must be >= 0 (got {delays[i]}).");
                }
            }
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
