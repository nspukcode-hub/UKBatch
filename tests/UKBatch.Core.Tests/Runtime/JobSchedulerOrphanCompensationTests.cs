using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using UKBatch;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Core.Tests.Helpers;
using UKBatch.Registry;
using UKBatch.Runtime;
using UKBatch.Storage;
using Xunit;

namespace UKBatch.Core.Tests.Runtime;

/// <summary>
/// A scheduler fire is two steps — create the execution row, then enqueue the work item — and the
/// enqueue can fail (dispatcher already draining at shutdown, backpressure wait cancelled). The
/// created row must then be compensated to a terminal state instead of sitting in Pending forever
/// as a live-process orphan.
/// </summary>
public class JobSchedulerOrphanCompensationTests
{
    private static JobDefinition Def(string name, string schedule) => new()
    {
        Name = name,
        ImplementationTypeName = typeof(object).AssemblyQualifiedName,
        IsPartitioned = false,
        Schedule = schedule,
        MaxRetries = 0,
        TimeoutSeconds = 0,
        PartitionWorkerCount = 0,
        ItemErrorPolicy = ItemErrorPolicy.FailFast,
        DefaultParameters = new Dictionary<string, object?>(),
        Tags = Array.Empty<string>(),
        SourceService = null,
    };

    [Fact]
    public async Task Fire_EnqueueRejectedByDrainingDispatcher_MarksCreatedExecutionFailed()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var registry = new JobDefinitionRegistry();
        registry.Register(Def("orphan.job", "* * * * * *"), typeof(object), null);
        var options = Options.Create(new UKBatchOptions { CronFormat = Cronos.CronFormat.IncludeSeconds });
        var watchHub = new JobExecutionWatchHub(NullLogger<JobExecutionWatchHub>.Instance);
        var store = new InMemoryJobStore(clock, options, watchHub);
        var dispatcher = new JobDispatcher(options, NullLogger<JobDispatcher>.Instance);
        var scheduler = new JobScheduler(
            registry, dispatcher, store, new CronExpressionCache(), clock,
            options, NullLogger<JobScheduler>.Instance);

        // The dispatcher rejects new triggers (the shutdown-drain posture) BEFORE the occurrence
        // fires, so the fire creates its execution row and then fails to enqueue it.
        dispatcher.StopAcceptingTriggers();
        await scheduler.StartAsync(default);
        try
        {
            clock.Advance(TimeSpan.FromSeconds(1));

            var compensated = await Waits.ForAsync(async () =>
            {
                var page = await store.QueryAsync(new JobQuery { JobName = "orphan.job" }, default).ConfigureAwait(false);
                return page.Count >= 1 && page.All(e => e.Status == JobStatus.Failed);
            }, TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            compensated.Should().BeTrue(
                "an execution row created by the scheduler must not be left Pending when its enqueue fails");

            var rows = await store.QueryAsync(new JobQuery { JobName = "orphan.job" }, default).ConfigureAwait(false);
            rows[0].LastError.Should().Contain("before the job could be enqueued");
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
