using Microsoft.Extensions.Logging;

namespace UKBatch.Internal;

/// <summary>
/// Helper for materialising per-job loggers:
/// the logger category is <c>"UKBatch.Job.{jobName}"</c> (so operators can filter per job),
/// and the runtime opens a <see cref="ILogger.BeginScope{TState}(TState)"/> with the structured
/// execution context (<c>ExecutionId</c>, <c>AttemptNumber</c>, <c>BatchId</c>, <c>BatchStepId</c>).
/// </summary>
internal static class JobLoggerFactory
{
    /// <summary>
    /// Returns a logger whose category is <c>"UKBatch.Job.{jobName}"</c>.
    /// </summary>
    public static ILogger CreateLogger(ILoggerFactory factory, string jobName)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrEmpty(jobName);
        return factory.CreateLogger($"UKBatch.Job.{jobName}");
    }

    /// <summary>
    /// Opens a structured logging scope carrying the per-execution context. Returns an
    /// <see cref="IDisposable"/> that the worker disposes when the execution ends.
    /// </summary>
    public static IDisposable? BeginExecutionScope(
        ILogger logger,
        string executionId,
        int attemptNumber,
        string? batchId,
        string? batchStepId)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrEmpty(executionId);
        var scopeState = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ExecutionId"] = executionId,
            ["AttemptNumber"] = attemptNumber,
            ["BatchId"] = batchId,
            ["BatchStepId"] = batchStepId,
        };
        return logger.BeginScope(scopeState);
    }
}
