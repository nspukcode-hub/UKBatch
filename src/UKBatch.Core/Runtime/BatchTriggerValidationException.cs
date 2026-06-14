namespace UKBatch.Runtime;

/// <summary>
/// Thrown synchronously inside <c>JobRunner.TriggerBatchAsync</c> (before the fire-and-forget run
/// begins) when a batch definition fails structural validation or references a job that is not
/// registered. Lets a trigger endpoint return 400 with the specific errors instead of accepting
/// the trigger and silently producing zero executions (the failure would otherwise only appear in
/// a server log). Derives from <see cref="InvalidOperationException"/>, consistent with the other
/// typed runtime exceptions.
/// </summary>
public sealed class BatchTriggerValidationException : InvalidOperationException
{
    /// <summary>Constructs the exception with a summary message and the structured per-field errors.</summary>
    public BatchTriggerValidationException(string message, IReadOnlyList<BatchTriggerValidationError> errors)
        : base(message) => Errors = errors;

    /// <summary>Structured per-field errors for the ProblemDetails body.</summary>
    public IReadOnlyList<BatchTriggerValidationError> Errors { get; init; } = [];
}

/// <summary>One validation error: the dotted path and a human-readable message.</summary>
public sealed record class BatchTriggerValidationError(string Path, string Message);
