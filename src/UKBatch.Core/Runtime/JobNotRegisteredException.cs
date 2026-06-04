namespace UKBatch.Runtime;

/// <summary>
/// Thrown by <c>JobRunner.TriggerInternalAsync</c> when the requested <c>jobName</c> is not in the
/// registry. Consumed by REST endpoints for the 404 <c>ukbatch:job-not-registered</c>
/// mapping on <c>POST /jobs/{name}/trigger</c>.
/// </summary>
/// <remarks>
/// Inherits <see cref="InvalidOperationException"/> for zero-test-churn — existing tests
/// asserting <c>Assert.ThrowsAsync&lt;InvalidOperationException&gt;</c> continue to pass.
/// </remarks>
public sealed class JobNotRegisteredException : InvalidOperationException
{
    /// <summary>The unregistered job name; <c>null</c> if the caller did not supply context.</summary>
    public string? JobName { get; init; }

    /// <summary>Constructs an exception with the given message.</summary>
    public JobNotRegisteredException(string message) : base(message) { }

    /// <summary>Constructs an exception with the given message and inner exception.</summary>
    public JobNotRegisteredException(string message, Exception innerException) : base(message, innerException) { }
}
