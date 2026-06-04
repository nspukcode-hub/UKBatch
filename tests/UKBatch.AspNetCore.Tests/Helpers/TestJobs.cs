using UKBatch.Abstractions.Jobs;
using UKBatch.AspNetCore.Tracing;

namespace UKBatch.AspNetCore.Tests.Helpers;

/// <summary>
/// Test-only job that captures the <see cref="JobContext.TriggeredBy"/> it sees for assertions.
/// Singleton instance — tests resolve via <c>app.Services.GetRequiredService&lt;CapturedTriggeredBy&gt;</c>.
/// </summary>
public sealed class CapturedTriggeredBy
{
    private readonly TaskCompletionSource<string?> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Pushed by <see cref="TriggeredByCapturingJob"/> on first execution.</summary>
    public void Record(string? value) => _tcs.TrySetResult(value);

    /// <summary>Awaits the first recorded value (or the configured timeout).</summary>
    public Task<string?> WaitAsync(TimeSpan timeout) => _tcs.Task.WaitAsync(timeout);
}

/// <summary>
/// Job that pushes its captured <see cref="JobContext.TriggeredBy"/> into the singleton
/// <see cref="CapturedTriggeredBy"/> service so the test can assert it.
/// </summary>
public sealed class TriggeredByCapturingJob : IJob
{
    private readonly CapturedTriggeredBy _sink;

    /// <summary>Injected DI ctor — the sink is registered as a singleton in the test host.</summary>
    public TriggeredByCapturingJob(CapturedTriggeredBy sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
    }

    /// <inheritdoc/>
    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var _ = context.RestoreRequestActivity();
        _sink.Record(context.TriggeredBy);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Singleton that holds the captured <see cref="System.Diagnostics.Activity"/> from inside a job
/// (post <c>RestoreRequestActivity</c>) so tests can assert trace correlation.
/// </summary>
public sealed class CapturedActivityInfo
{
    private readonly TaskCompletionSource<(string? TraceId, string? ParentId, string? OperationName)> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Pushed by <see cref="ActivityCapturingJob"/>.</summary>
    public void Record(string? traceId, string? parentId, string? operationName)
        => _tcs.TrySetResult((traceId, parentId, operationName));

    /// <summary>Awaits the first recorded value.</summary>
    public Task<(string? TraceId, string? ParentId, string? OperationName)> WaitAsync(TimeSpan timeout)
        => _tcs.Task.WaitAsync(timeout);
}

/// <summary>
/// Job that inspects <see cref="System.Diagnostics.Activity.Current"/> after calling
/// <c>RestoreRequestActivity</c> and pushes the trace ids into <see cref="CapturedActivityInfo"/>.
/// </summary>
public sealed class ActivityCapturingJob : IJob
{
    private readonly CapturedActivityInfo _sink;

    /// <summary>Constructs the job. <paramref name="sink"/> is the singleton sink.</summary>
    public ActivityCapturingJob(CapturedActivityInfo sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
    }

    /// <inheritdoc/>
    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var _ = context.RestoreRequestActivity();
        var current = System.Diagnostics.Activity.Current;
        _sink.Record(current?.TraceId.ToString(), current?.ParentId, current?.OperationName);
        return Task.CompletedTask;
    }
}
