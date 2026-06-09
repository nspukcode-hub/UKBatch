using UKBatch.Abstractions.Jobs;

namespace UKBatch.Core.Tests.Helpers;

/// <summary>
/// Reusable test job implementations. Each tracks an Interlocked invocation counter so tests
/// can assert dispatch/run counts deterministically.
/// </summary>
public sealed class SucceedingJob : IJob
{
    public static int InvocationCount;
    public static readonly List<string> Names = new();
    private static readonly object _lock = new();

    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref InvocationCount);
        lock (_lock) { Names.Add(context.JobName); }
        return Task.CompletedTask;
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref InvocationCount, 0);
        lock (_lock) { Names.Clear(); }
    }
}

public sealed class FailingJob : IJob
{
    public static int InvocationCount;

    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref InvocationCount);
        throw new InvalidOperationException("intentional failure");
    }

    public static void Reset() => Interlocked.Exchange(ref InvocationCount, 0);
}

public sealed class TransientThenSucceedJob : IJob
{
    public static int InvocationCount;
    public static int FailUntilAttempt = 2;

    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var n = Interlocked.Increment(ref InvocationCount);
        if (context.AttemptNumber < FailUntilAttempt)
        {
            throw new InvalidOperationException($"transient failure (attempt {context.AttemptNumber})");
        }
        return Task.CompletedTask;
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref InvocationCount, 0);
        FailUntilAttempt = 2;
    }
}

public sealed class CountingPartitionedJob : IPartitionedJob<int>
{
    private readonly int _itemCount;
    public CountingPartitionedJob(int itemCount) { _itemCount = itemCount; }
    public CountingPartitionedJob() : this(0) { }

    public static int Total;
    public static int FailAt = -1;
    public static int FailCount;
    public static long Processed;

    public async IAsyncEnumerable<int> SourceAsync(
        JobContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var count = _itemCount > 0 ? _itemCount : Total;
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return i;
            await Task.Yield();
        }
    }

    public Task ProcessAsync(int item, JobContext context, CancellationToken cancellationToken)
    {
        if (FailAt >= 0 && item == FailAt)
        {
            Interlocked.Increment(ref FailCount);
            throw new InvalidOperationException($"intentional failure at item {item}");
        }
        Interlocked.Increment(ref Processed);
        return Task.CompletedTask;
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref Total, 0);
        Interlocked.Exchange(ref FailAt, -1);
        Interlocked.Exchange(ref FailCount, 0);
        Interlocked.Exchange(ref Processed, 0);
    }
}
