namespace UKBatch.Validation;

/// <summary>
/// Single validation failure entry. Carried inside <see cref="ValidationResult.Errors"/>.
/// </summary>
internal sealed record class ValidationError(string PropertyPath, string Message);
