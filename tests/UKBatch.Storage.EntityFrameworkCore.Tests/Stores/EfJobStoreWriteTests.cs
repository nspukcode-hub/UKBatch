using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Storage;
using UKBatch.Storage.EntityFrameworkCore.Stores;
using UKBatch.Storage.EntityFrameworkCore.Tests.Infrastructure;
using Xunit;

namespace UKBatch.Storage.EntityFrameworkCore.Tests.Stores;

/// <summary>
/// <see cref="EfJobStore"/> writer behavior on SQLite: Create / Insert / UpdateStatus (legal +
/// illegal-transition reject via <see cref="JobStatusTransitions"/>) / RecordAttempt / UpdateProgress;
/// terminal timestamps; StartedAt set-once.
/// </summary>
public sealed class EfJobStoreWriteTests : IAsyncLifetime
{
    private SqliteStoreHarness _harness = default!;
    private EfJobStore _store = default!;
    private JobExecutionWatchHub _hub = default!;

    public async Task InitializeAsync()
    {
        _harness = await SqliteStoreHarness.CreateAsync();
        _hub = new JobExecutionWatchHub(NullLogger<JobExecutionWatchHub>.Instance);
        _store = new EfJobStore(_harness.Factory, _hub, _harness.Clock, NullLogger<EfJobStore>.Instance);
    }

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    public async Task CreateAsync_NewExecution_PersistsPendingWithUuidv7Id()
    {
        var created = await _store.CreateAsync(TestData.JobDef("my.job", maxRetries: 3), CancellationToken.None);

        created.Status.Should().Be(JobStatus.Pending);
        created.JobName.Should().Be("my.job");
        created.MaxRetries.Should().Be(3);
        created.ExecutionId.Should().HaveLength(32, "UUIDv7 'N' format");
        created.EnqueuedAtUtc.Should().Be(_harness.Clock.GetUtcNow());

        var fetched = await _store.GetAsync(created.ExecutionId, CancellationToken.None);
        fetched.Should().NotBeNull();
        fetched!.Status.Should().Be(JobStatus.Pending);
    }

    [Fact]
    public async Task CreateAsync_NullDefinition_Throws()
    {
        var act = async () => await _store.CreateAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task InsertAsync_RoundTripsAllFields()
    {
        var exec = TestData.Execution("ins-1", jobName: "batch.job", batchId: "run-1", batchStepId: "s-1",
            batchDefinitionId: "def-X", status: JobStatus.Pending, workerName: "w-1", triggeredBy: "user@x");

        await _store.InsertAsync(exec, CancellationToken.None);

        var fetched = await _store.GetAsync("ins-1", CancellationToken.None);
        fetched.Should().NotBeNull();
        fetched!.BatchId.Should().Be("run-1");
        fetched.BatchStepId.Should().Be("s-1");
        fetched.BatchDefinitionId.Should().Be("def-X");
        fetched.WorkerName.Should().Be("w-1");
        fetched.TriggeredBy.Should().Be("user@x");
    }

    [Fact]
    public async Task UpdateStatusAsync_PendingToRunning_SetsStartedAt()
    {
        await _store.InsertAsync(TestData.Execution("e1", status: JobStatus.Pending), CancellationToken.None);

        _harness.Clock.Advance(TimeSpan.FromSeconds(5));
        await _store.UpdateStatusAsync("e1", JobStatus.Running, null, CancellationToken.None);

        var fetched = await _store.GetAsync("e1", CancellationToken.None);
        fetched!.Status.Should().Be(JobStatus.Running);
        fetched.StartedAtUtc.Should().Be(_harness.Clock.GetUtcNow());
        fetched.CompletedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task UpdateStatusAsync_StartedAt_SetOnce_NotOverwrittenOnSubsequentRunning()
    {
        await _store.InsertAsync(TestData.Execution("e1", status: JobStatus.Pending), CancellationToken.None);
        _harness.Clock.Advance(TimeSpan.FromSeconds(5));
        await _store.UpdateStatusAsync("e1", JobStatus.Running, null, CancellationToken.None);
        var firstStart = (await _store.GetAsync("e1", CancellationToken.None))!.StartedAtUtc;

        // Running -> Retrying -> Running again; the second Running must NOT reset StartedAt.
        await _store.UpdateStatusAsync("e1", JobStatus.Retrying, null, CancellationToken.None);
        _harness.Clock.Advance(TimeSpan.FromSeconds(30));
        await _store.UpdateStatusAsync("e1", JobStatus.Running, null, CancellationToken.None);

        var fetched = await _store.GetAsync("e1", CancellationToken.None);
        fetched!.StartedAtUtc.Should().Be(firstStart, "StartedAt is set-once on first Running");
    }

    [Fact]
    public async Task UpdateStatusAsync_ToTerminal_SetsCompletedAt()
    {
        await _store.InsertAsync(TestData.Execution("e1", status: JobStatus.Pending), CancellationToken.None);
        await _store.UpdateStatusAsync("e1", JobStatus.Running, null, CancellationToken.None);

        _harness.Clock.Advance(TimeSpan.FromSeconds(10));
        await _store.UpdateStatusAsync("e1", JobStatus.Completed, null, CancellationToken.None);

        var fetched = await _store.GetAsync("e1", CancellationToken.None);
        fetched!.Status.Should().Be(JobStatus.Completed);
        fetched.CompletedAtUtc.Should().Be(_harness.Clock.GetUtcNow());
    }

    [Fact]
    public async Task UpdateStatusAsync_FailedTerminal_SetsCompletedAtAndError()
    {
        await _store.InsertAsync(TestData.Execution("e1", status: JobStatus.Pending), CancellationToken.None);
        await _store.UpdateStatusAsync("e1", JobStatus.Running, null, CancellationToken.None);
        await _store.UpdateStatusAsync("e1", JobStatus.Failed, "boom", CancellationToken.None);

        var fetched = await _store.GetAsync("e1", CancellationToken.None);
        fetched!.Status.Should().Be(JobStatus.Failed);
        fetched.LastError.Should().Be("boom");
        fetched.CompletedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateStatusAsync_IllegalTransition_ThrowsInvalidOperation()
    {
        // Pending -> Completed is NOT a legal edge (Pending -> {Running, Cancelling, Failed}).
        await _store.InsertAsync(TestData.Execution("e1", status: JobStatus.Pending), CancellationToken.None);

        var act = async () => await _store.UpdateStatusAsync("e1", JobStatus.Completed, null, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Illegal job status transition*");
    }

    [Fact]
    public async Task UpdateStatusAsync_IllegalTransition_FromTerminal_Throws()
    {
        await _store.InsertAsync(TestData.Execution("e1", status: JobStatus.Pending), CancellationToken.None);
        await _store.UpdateStatusAsync("e1", JobStatus.Running, null, CancellationToken.None);
        await _store.UpdateStatusAsync("e1", JobStatus.Completed, null, CancellationToken.None);

        // Completed is terminal — no outgoing edge.
        var act = async () => await _store.UpdateStatusAsync("e1", JobStatus.Running, null, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task UpdateStatusAsync_BaseInvalidOperationException_NotCoreInternalSubtype()
    {
        // The frozen IJobExecutionWriter contract promises InvalidOperationException; the EF adapter
        // must throw exactly that (via public JobStatusTransitions.Validate), NOT a Core-internal subtype.
        await _store.InsertAsync(TestData.Execution("e1", status: JobStatus.Pending), CancellationToken.None);
        var act = async () => await _store.UpdateStatusAsync("e1", JobStatus.Completed, null, CancellationToken.None);
        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.GetType().Should().Be(typeof(InvalidOperationException),
            "adapter uses the public matrix exception, not Core's internal InvalidJobTransitionException subtype");
    }

    [Fact]
    public async Task UpdateStatusAsync_MissingExecution_ThrowsKeyNotFound()
    {
        var act = async () => await _store.UpdateStatusAsync("nope", JobStatus.Running, null, CancellationToken.None);
        await act.Should().ThrowAsync<KeyNotFoundException>().WithMessage("*not found*");
    }

    [Fact]
    public async Task UpdateStatusAsync_NullError_PreservesExistingLastError()
    {
        await _store.InsertAsync(TestData.Execution("e1", status: JobStatus.Pending, lastError: "prior"), CancellationToken.None);
        await _store.UpdateStatusAsync("e1", JobStatus.Running, null, CancellationToken.None);

        var fetched = await _store.GetAsync("e1", CancellationToken.None);
        fetched!.LastError.Should().Be("prior", "a null errorMessage must not clobber an existing LastError");
    }

    [Fact]
    public async Task RecordAttemptAsync_UpdatesAttemptNumber()
    {
        await _store.InsertAsync(TestData.Execution("e1", attemptNumber: 1), CancellationToken.None);
        await _store.RecordAttemptAsync("e1", 3, CancellationToken.None);

        var fetched = await _store.GetAsync("e1", CancellationToken.None);
        fetched!.AttemptNumber.Should().Be(3);
    }

    [Fact]
    public async Task RecordAttemptAsync_MissingExecution_ThrowsKeyNotFound()
    {
        var act = async () => await _store.RecordAttemptAsync("nope", 2, CancellationToken.None);
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task UpdateProgressAsync_UpdatesCounters()
    {
        await _store.InsertAsync(TestData.Execution("e1"), CancellationToken.None);
        await _store.UpdateProgressAsync("e1", processed: 42, failed: 3, total: 100, CancellationToken.None);

        var fetched = await _store.GetAsync("e1", CancellationToken.None);
        fetched!.Processed.Should().Be(42);
        fetched.Failed.Should().Be(3);
        fetched.Total.Should().Be(100);
    }

    [Fact]
    public async Task UpdateProgressAsync_MissingExecution_ThrowsKeyNotFound()
    {
        var act = async () => await _store.UpdateProgressAsync("nope", 1, 0, null, CancellationToken.None);
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task GetAsync_MissingExecution_ReturnsNull()
    {
        var fetched = await _store.GetAsync("does-not-exist", CancellationToken.None);
        fetched.Should().BeNull();
    }
}
