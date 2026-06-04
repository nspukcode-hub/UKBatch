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
        return new ValidationResult(errors);
    }
}
