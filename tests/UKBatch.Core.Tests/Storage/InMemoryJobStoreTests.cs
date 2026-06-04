using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UKBatch;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Runtime;
using UKBatch.Storage;
using Xunit;

namespace UKBatch.Core.Tests.Storage;

/// <summary>
/// CRUD-level tests for <see cref="InMemoryJobStore"/>. State-machine integration covered separately.
/// </summary>
public class InMemoryJobStoreTests
{
    private static InMemoryJobStore CreateStore()
    {
        var clock = TimeProvider.System;
        var options = Options.Create(new UKBatchOptions());
        return new InMemoryJobStore(clock, options, new JobExecutionWatchHub(NullLogger<JobExecutionWatchHub>.Instance));
    }

    private static JobDefinition NewDef(string name = "Test.Job") => new()
    {
        Name = name,
        IsPartitioned = false,
        MaxRetries = 3,
        TimeoutSeconds = 0,
        DefaultParameters = new Dictionary<string, object?>(),
        Tags = Array.Empty<string>(),
    };

    [Fact]
    public async Task CreateAsync_NewExecution_ReturnsPendingStatus()
    {
        var store = CreateStore();
        var def = NewDef();

        var execution = await store.CreateAsync(def, default).ConfigureAwait(false);

        execution.Status.Should().Be(JobStatus.Pending);
        execution.JobName.Should().Be("Test.Job");
        execution.AttemptNumber.Should().Be(1);
        execution.MaxRetries.Should().Be(3);
        execution.Processed.Should().Be(0);
        execution.Failed.Should().Be(0);
        execution.Total.Should().BeNull();
        execution.LastError.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_AssignsUniqueExecutionId()
    {
        var store = CreateStore();
        var def = NewDef();

        var e1 = await store.CreateAsync(def, default).ConfigureAwait(false);
        var e2 = await store.CreateAsync(def, default).ConfigureAwait(false);

        e1.ExecutionId.Should().NotBe(e2.ExecutionId);
    }

    [Fact]
    public async Task GetAsync_UnknownId_ReturnsNull()
    {
        var store = CreateStore();
        var result = await store.GetAsync("nonexistent", default).ConfigureAwait(false);
        result.Should().BeNull();
    }

    [Fact]
    public async Task UpdateStatusAsync_LegalTransition_UpdatesStatus()
    {
        var store = CreateStore();
        var def = NewDef();
        var execution = await store.CreateAsync(def, default).ConfigureAwait(false);

        await store.UpdateStatusAsync(execution.ExecutionId, JobStatus.Running, null, default).ConfigureAwait(false);

        var updated = await store.GetAsync(execution.ExecutionId, default).ConfigureAwait(false);
        updated.Should().NotBeNull();
        updated!.Status.Should().Be(JobStatus.Running);
        updated.StartedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateStatusAsync_IllegalTransition_ThrowsInvalidJobTransitionException()
    {
        var store = CreateStore();
        var def = NewDef();
        var execution = await store.CreateAsync(def, default).ConfigureAwait(false);

        Func<Task> act = async () =>
            await store.UpdateStatusAsync(execution.ExecutionId, JobStatus.Completed, null, default).ConfigureAwait(false);

        await act.Should().ThrowAsync<InvalidJobTransitionException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task UpdateStatusAsync_UnknownExecutionId_ThrowsKeyNotFoundException()
    {
        var store = CreateStore();

        Func<Task> act = async () =>
            await store.UpdateStatusAsync("nonexistent", JobStatus.Running, null, default).ConfigureAwait(false);

        await act.Should().ThrowAsync<KeyNotFoundException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task UpdateStatusAsync_TerminalTransition_SetsCompletedAtUtc()
    {
        var store = CreateStore();
        var def = NewDef();
        var execution = await store.CreateAsync(def, default).ConfigureAwait(false);

        await store.UpdateStatusAsync(execution.ExecutionId, JobStatus.Running, null, default).ConfigureAwait(false);
        await store.UpdateStatusAsync(execution.ExecutionId, JobStatus.Completed, null, default).ConfigureAwait(false);

        var updated = await store.GetAsync(execution.ExecutionId, default).ConfigureAwait(false);
        updated!.CompletedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateStatusAsync_WithErrorMessage_StoresLastError()
    {
        var store = CreateStore();
        var def = NewDef();
        var execution = await store.CreateAsync(def, default).ConfigureAwait(false);

        await store.UpdateStatusAsync(execution.ExecutionId, JobStatus.Running, null, default).ConfigureAwait(false);
        await store.UpdateStatusAsync(execution.ExecutionId, JobStatus.Failed, "boom", default).ConfigureAwait(false);

        var updated = await store.GetAsync(execution.ExecutionId, default).ConfigureAwait(false);
        updated!.LastError.Should().Be("boom");
        updated.Status.Should().Be(JobStatus.Failed);
    }

    [Fact]
    public async Task UpdateProgressAsync_UpdatesProcessedFailedTotal_Flat()
    {
        // Fix 4 / N- — flat fields on JobExecution (not nested Progress).
        var store = CreateStore();
        var def = NewDef();
        var execution = await store.CreateAsync(def, default).ConfigureAwait(false);

        await store.UpdateProgressAsync(execution.ExecutionId, 42, 3, 100, default).ConfigureAwait(false);

        var updated = await store.GetAsync(execution.ExecutionId, default).ConfigureAwait(false);
        updated!.Processed.Should().Be(42);
        updated.Failed.Should().Be(3);
        updated.Total.Should().Be(100);
    }

    [Fact]
    public async Task RecordAttemptAsync_BumpsAttemptNumber()
    {
        var store = CreateStore();
        var def = NewDef();
        var execution = await store.CreateAsync(def, default).ConfigureAwait(false);

        await store.RecordAttemptAsync(execution.ExecutionId, 2, default).ConfigureAwait(false);

        var updated = await store.GetAsync(execution.ExecutionId, default).ConfigureAwait(false);
        updated!.AttemptNumber.Should().Be(2);
    }

    [Fact]
    public async Task QueryAsync_FilterByStatus_ReturnsMatchingOnly()
    {
        var store = CreateStore();
        var def = NewDef();
        var e1 = await store.CreateAsync(def, default).ConfigureAwait(false);
        var e2 = await store.CreateAsync(def, default).ConfigureAwait(false);
        await store.UpdateStatusAsync(e1.ExecutionId, JobStatus.Running, null, default).ConfigureAwait(false);

        var results = await store.QueryAsync(new JobQuery
        {
            Statuses = new[] { JobStatus.Running },
            Limit = 10,
        }, default).ConfigureAwait(false);

        results.Should().HaveCount(1);
        results[0].ExecutionId.Should().Be(e1.ExecutionId);
        _ = e2;
    }

    [Fact]
    public async Task CountAsync_AppliesSameFilter()
    {
        var store = CreateStore();
        var def = NewDef();
        for (var i = 0; i < 5; i++)
        {
            _ = await store.CreateAsync(def, default).ConfigureAwait(false);
        }

        var count = await store.CountAsync(new JobQuery { Statuses = new[] { JobStatus.Pending } }, default).ConfigureAwait(false);
        count.Should().Be(5);
    }

    [Fact]
    public async Task QueryAsync_PagesByOffsetAndLimit()
    {
        var store = CreateStore();
        var def = NewDef();
        for (var i = 0; i < 10; i++)
        {
            _ = await store.CreateAsync(def, default).ConfigureAwait(false);
        }

        var page1 = await store.QueryAsync(new JobQuery { Offset = 0, Limit = 3 }, default).ConfigureAwait(false);
        var page2 = await store.QueryAsync(new JobQuery { Offset = 3, Limit = 3 }, default).ConfigureAwait(false);

        page1.Should().HaveCount(3);
        page2.Should().HaveCount(3);
        page1.Select(e => e.ExecutionId).Should().NotIntersectWith(page2.Select(e => e.ExecutionId));
    }
}
