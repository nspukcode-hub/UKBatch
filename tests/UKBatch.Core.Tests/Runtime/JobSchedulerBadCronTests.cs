using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UKBatch;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Core.Tests.Helpers;
using UKBatch.Registry;
using UKBatch.Runtime;
using UKBatch.Storage;
using Xunit;

namespace UKBatch.Core.Tests.Runtime;

/// <summary>
/// Defense-in-depth: a malformed cron that somehow reaches the scheduler must NOT take down host
/// startup. <c>StartAsync</c> logs an error naming the bad job and skips ONLY that definition; every
/// other scheduled job still arms.
/// </summary>
public class JobSchedulerBadCronTests
{
    private sealed class CapturingLogger : ILogger<JobScheduler>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) { Entries.Add((logLevel, formatter(state, exception))); }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

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
    public async Task StartAsync_BadCronDefinition_DoesNotThrow_LogsError_AndGoodJobArms()
    {
        var registry = new JobDefinitionRegistry();
        // The registry itself does not validate cron, so a bad expression can be injected here to
        // simulate a definition that bypassed registration-time validation.
        registry.Register(Def("good.job", "* * * * * *"), typeof(object), null);     // valid IncludeSeconds, fires every second
        registry.Register(Def("bad.job", "not a cron at all"), typeof(object), null); // bad → must be skipped

        var options = Options.Create(new UKBatchOptions { CronFormat = Cronos.CronFormat.IncludeSeconds });
        var watchHub = new JobExecutionWatchHub(NullLogger<JobExecutionWatchHub>.Instance);
        var store = new InMemoryJobStore(TimeProvider.System, options, watchHub);
        var dispatcher = new JobDispatcher(options, NullLogger<JobDispatcher>.Instance);
        var cronCache = new CronExpressionCache();
        var logger = new CapturingLogger();

        var scheduler = new JobScheduler(
            registry, dispatcher, store, cronCache, TimeProvider.System, options, logger);

        // Must NOT throw despite the bad-cron definition.
        Func<Task> start = () => scheduler.StartAsync(default);
        await start.Should().NotThrowAsync().ConfigureAwait(false);

        try
        {
            // The bad definition was logged at Error level naming the job + the offending expression.
            await Waits.ForAsync(() =>
            {
                lock (logger.Entries)
                {
                    return logger.Entries.Any(e =>
                        e.Level == LogLevel.Error
                        && e.Message.Contains("bad.job", StringComparison.Ordinal)
                        && e.Message.Contains("not a cron at all", StringComparison.Ordinal));
                }
            }, TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            lock (logger.Entries)
            {
                logger.Entries.Should().Contain(e =>
                    e.Level == LogLevel.Error && e.Message.Contains("bad.job", StringComparison.Ordinal));
            }

            // The good job still armed: its every-second cron must fire and create an execution.
            var goodFired = await Waits.ForAsync(async () =>
            {
                var page = await store.QueryAsync(new JobQuery { JobName = "good.job" }, default).ConfigureAwait(false);
                return page.Any();
            }, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            goodFired.Should().BeTrue("the valid scheduled job must still arm and fire after the bad one is skipped.");
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
