using System.Runtime.CompilerServices;
using UKBatch.Abstractions.Jobs;

namespace Sample.Dashboard.Jobs;

/// <summary>
/// Partitioned job fixture (mirrors Sample.RestApi). Surfaces in <c>GET /jobs?partitioned=true</c>
/// for partitioned-filter test coverage and for the Dashboard jobs catalog demo.
/// </summary>
public sealed class BulkArchiveJob : IPartitionedJob<string>
{
    /// <inheritdoc/>
    public async IAsyncEnumerable<string> SourceAsync(
        JobContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        for (var i = 1; i <= 10; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return $"invoice-{i}";
            await Task.Yield();
        }
    }

    /// <inheritdoc/>
    public Task ProcessAsync(string item, JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Logger.LogInformation("Archived {Invoice}", item);
        return Task.CompletedTask;
    }
}
