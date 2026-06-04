namespace UKBatch.Validation;

/// <summary>
/// Aggregated validation outcome. <see cref="IsValid"/> is <c>true</c> iff
/// <see cref="Errors"/> is empty.
/// </summary>
internal sealed record class ValidationResult
{
    /// <summary>Per-error entries; empty list means success.</summary>
    public IReadOnlyList<ValidationError> Errors { get; }

    /// <summary>True iff no errors were collected.</summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>Constructs a result from a collected error list.</summary>
    public ValidationResult(IReadOnlyList<ValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        Errors = errors;
    }
}
