namespace UKBatch.Abstractions.Jobs;

/// <summary>
/// Single unit of background work. Implementations are resolved per execution from the per-job DI scope on <see cref="JobContext.Services"/>.
/// Implementations MUST be safe to instantiate fresh per execution; they MUST NOT carry mutable instance state across executions.
/// </summary>
public interface IJob
{
    /// <summary>
    /// Executes the job. Implementations MUST honour <paramref name="cancellationToken"/> and propagate it
    /// to every awaitable I/O call. Returning early on cancellation is preferred over swallowing the token.
    /// </summary>
    Task ExecuteAsync(JobContext context, CancellationToken cancellationToken);
}
