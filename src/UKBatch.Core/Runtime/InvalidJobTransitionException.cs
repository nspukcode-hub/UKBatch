using UKBatch.Abstractions.Models;

namespace UKBatch.Runtime;

/// <summary>
/// Thrown by <see cref="BatchStateMachine.Validate"/> when an illegal status transition is
/// attempted. Derives from <see cref="InvalidOperationException"/> so adapter writers can rely
/// on the frozen Abstractions contract that stores throw <c>InvalidOperationException</c> on
/// illegal transitions.
/// </summary>
internal sealed class InvalidJobTransitionException : InvalidOperationException
{
    /// <summary>Source status.</summary>
    public JobStatus From { get; }

    /// <summary>Attempted target status.</summary>
    public JobStatus To { get; }

    /// <summary>Constructs an exception describing an invalid transition.</summary>
    public InvalidJobTransitionException(JobStatus from, JobStatus to)
        : base($"Illegal job status transition: {from} -> {to}.")
    {
        From = from;
        To = to;
    }
}
