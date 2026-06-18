using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Runtime;
using UKBatch.Storage.EntityFrameworkCore.Recovery;
using UKBatch.Storage.EntityFrameworkCore.Stores;
using UKBatch.Storage.EntityFrameworkCore.Tests.Infrastructure;
using Xunit;

namespace UKBatch.Storage.EntityFrameworkCore.Tests.Core;

/// <summary>
/// <see cref="DurableRunRecovery"/> startup behavior in isolation (the EF-layer hosted service that
/// re-launches in-flight runs): the opt-out flag suppresses it; an in-flight run triggers a
/// <c>ResumeBatchAsync(ResumeForward)</c>; a terminal/completed run is left alone; one un-resumable run
/// (its <c>ResumeBatchAsync</c> throws, e.g. a missing definition) is logged and skipped without aborting
/// the others or throwing out of <c>StartAsync</c>; an empty store is a no-op.
/// </summary>
public sealed class DurableRunRecoveryTests : IAsyncLifetime
{
    private SqliteStoreHarness _db = default!;
    private EfBatchRunStore _runStore = default!;

    public async Task InitializeAsync()
    {
        _db = await SqliteStoreHarness.CreateAsync();
        _runStore = new EfBatchRunStore(_db.Factory);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private DurableRunRecovery NewRecovery(SpyRunner runner, bool enabled = true)
    {
        var options = new EfStorageOptions();
        options.UseSqlite("DataSource=:memory:");   // satisfies the validator; harness owns the real connection
        options.ResumeInFlightRunsOnStartup = enabled;
        return new DurableRunRecovery(
            _db.Factory, options, new SingleServiceProvider(runner), NullLogger<DurableRunRecovery>.Instance);
    }

    private async Task SeedRunAsync(string runId, JobStatus? status, DateTimeOffset? completedAt = null)
    {
        await _runStore.CreateAsync(new BatchRun
        {
            BatchId = runId,
            BatchDefinitionId = "def-1",
            BatchName = "pipeline",
            Status = null,
            TriggeredBy = "tester",
            StartedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            CompletedAtUtc = null,
            CurrentStepIndex = 1,
            StepCount = 2,
            Total = 0,
            Succeeded = 0,
            Failed = 0,
            Cancelled = 0,
        }, CancellationToken.None);

        if (status is not null)
        {
            await _runStore.CompleteAsync(
                runId, status.Value, new BatchRunCounts(0, 0, 0, 0),
                completedAt ?? new DateTimeOffset(2026, 1, 1, 0, 1, 0, TimeSpan.Zero), CancellationToken.None);
        }
    }

    [Fact]
    public async Task Disabled_DoesNotResume()
    {
        await SeedRunAsync("run-1", status: null);
        var runner = new SpyRunner();

        await NewRecovery(runner, enabled: false).StartAsync(CancellationToken.None);

        runner.Resumed.Should().BeEmpty("ResumeInFlightRunsOnStartup=false suppresses recovery");
    }

    [Fact]
    public async Task InflightRun_IsResumed_WithResumeForward()
    {
        await SeedRunAsync("run-1", status: null);
        var runner = new SpyRunner();

        await NewRecovery(runner).StartAsync(CancellationToken.None);

        runner.Resumed.Should().ContainSingle().Which.Should().Be(("run-1", ResumePolicy.ResumeForward),
            "an in-flight run is resumed with ResumeForward");
    }

    [Fact]
    public async Task TerminalRun_IsNotResumed()
    {
        await SeedRunAsync("run-done", status: JobStatus.Completed);
        var runner = new SpyRunner();

        await NewRecovery(runner).StartAsync(CancellationToken.None);

        runner.Resumed.Should().BeEmpty("a completed run is not in-flight and must not be resumed");
    }

    [Fact]
    public async Task EmptyStore_IsNoOp()
    {
        var runner = new SpyRunner();

        await NewRecovery(runner).StartAsync(CancellationToken.None);

        runner.Resumed.Should().BeEmpty("no in-flight runs → nothing to resume");
    }

    [Fact]
    public async Task UnresumableRun_IsLoggedAndSkipped_DoesNotThrow_AndContinuesOthers()
    {
        // Two in-flight runs; the first throws when resumed (e.g. its definition no longer exists). Recovery
        // must log and continue to the second, and StartAsync must NOT throw.
        await SeedRunAsync("run-bad", status: null);
        await SeedRunAsync("run-good", status: null);
        var runner = new SpyRunner { ThrowFor = "run-bad" };

        var act = async () => await NewRecovery(runner).StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync("one un-resumable run must not abort recovery");
        runner.AllAttempted.Should().Contain("run-bad", "the failing run was attempted");
        runner.Resumed.Select(r => r.batchId).Should().Contain("run-good",
            "the second run is still resumed after the first one fails");
    }

    /// <summary>An <see cref="IJobRunner"/> spy that records resume calls; recovery uses only ResumeBatchAsync.</summary>
    private sealed class SpyRunner : IJobRunner
    {
        public ConcurrentQueue<(string batchId, ResumePolicy policy)> Resumed { get; } = new();
        public ConcurrentQueue<string> AllAttempted { get; } = new();
        public string? ThrowFor { get; set; }

        public Task ResumeBatchAsync(string batchId, ResumePolicy policy, CancellationToken cancellationToken)
        {
            AllAttempted.Enqueue(batchId);
            if (batchId == ThrowFor)
            {
                throw new BatchDefinitionNotFoundException($"definition for {batchId} not found");
            }
            Resumed.Enqueue((batchId, policy));
            return Task.CompletedTask;
        }

        public Task<JobExecution> TriggerAsync(string jobName, JobParameters parameters, string? triggeredBy, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<string> TriggerBatchAsync(string batchDefinitionId, JobParameters? initialParameters, string? triggeredBy, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task CancelAsync(string executionId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    /// <summary>Minimal <see cref="IServiceProvider"/> that resolves the one service recovery asks for (IJobRunner).</summary>
    private sealed class SingleServiceProvider : IServiceProvider
    {
        private readonly IJobRunner _runner;
        public SingleServiceProvider(IJobRunner runner) => _runner = runner;
        public object? GetService(Type serviceType) => serviceType == typeof(IJobRunner) ? _runner : null;
    }
}
