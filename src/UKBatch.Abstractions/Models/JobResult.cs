namespace UKBatch.Abstractions.Models;

/// <summary>
/// Result envelope returned from a <see cref="Transport.ITransport.RequestReplyAsync"/> call.
/// </summary>
public sealed record class JobResult
{
    /// <summary>Correlated execution id on the responder.</summary>
    public required string ExecutionId { get; init; }

    /// <summary>Terminal status when the response was emitted.</summary>
    public required JobStatus Status { get; init; }

    /// <summary>Optional structured return payload; values MUST be JSON-serializable.</summary>
    public IReadOnlyDictionary<string, object?>? ReturnValues { get; init; }

    /// <summary>Error message when <see cref="Status"/> is <see cref="JobStatus.Failed"/>; <c>null</c> otherwise.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Reply-path headers — trace propagation, signing. Same reserved-key conventions as
    /// <see cref="Transport.JobMessage.Headers"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>UTC completion timestamp.</summary>
    public required DateTimeOffset CompletedAtUtc { get; init; }
}
