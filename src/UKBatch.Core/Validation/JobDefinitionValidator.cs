using UKBatch.Abstractions.Models;

namespace UKBatch.Validation;

/// <summary>
/// Stateless validator for <see cref="JobDefinition"/>. Used by <c>JobDefinitionFactory</c>
/// at registration time and by REST endpoints constructing JobDefinitions externally.
/// </summary>
internal static class JobDefinitionValidator
{
    /// <summary>Runs every rule and returns the aggregated result.</summary>
    public static ValidationResult Validate(JobDefinition def)
    {
        ArgumentNullException.ThrowIfNull(def);

        var errors = new List<ValidationError>();
        if (string.IsNullOrWhiteSpace(def.Name))
        {
            errors.Add(new ValidationError("Name", "must be non-empty"));
        }
        if (def.MaxRetries < 0)
        {
            errors.Add(new ValidationError("MaxRetries", "must be >= 0"));
        }
        if (def.TimeoutSeconds < 0)
        {
            errors.Add(new ValidationError("TimeoutSeconds", "must be >= 0"));
        }
        if (def.IsPartitioned && def.PartitionWorkerCount < 1)
        {
            errors.Add(new ValidationError("PartitionWorkerCount", "must be >= 1 for partitioned jobs"));
        }
        if (!Enum.IsDefined(def.ItemErrorPolicy))
        {
            errors.Add(new ValidationError("ItemErrorPolicy", $"unknown enum value {(int)def.ItemErrorPolicy}"));
        }
        if (def.DeclaredParameters.Count > 0)
        {
            // Shape checks over DECLARED names only (never trigger-time keys). The reserved 'ukbatch.'
            // prefix is off-limits so declared parameters cannot collide with framework-owned keys.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in def.DeclaredParameters)
            {
                if (string.IsNullOrWhiteSpace(p.Name))
                {
                    errors.Add(new ValidationError("DeclaredParameters", "a declared parameter name must be non-empty"));
                    continue;
                }
                if (p.Name.StartsWith("ukbatch.", StringComparison.Ordinal))
                {
                    errors.Add(new ValidationError($"DeclaredParameters['{p.Name}']", "must not use the reserved 'ukbatch.' prefix"));
                }
                if (!seen.Add(p.Name))
                {
                    errors.Add(new ValidationError($"DeclaredParameters['{p.Name}']", "duplicate declared parameter name"));
                }
            }
        }
        return new ValidationResult(errors);
    }
}
