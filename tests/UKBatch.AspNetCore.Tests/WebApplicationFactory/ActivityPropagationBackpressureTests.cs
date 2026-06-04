using System.Diagnostics;
using FluentAssertions;
using NSubstitute;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.AspNetCore.Triggering;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.AspNetCore.Tests.WebApplicationFactory;

/// <summary>
/// invariant — <see cref="JobRunnerHttpContextExtensions.TriggerWithRequestContextAsync"/> and
/// <see cref="JobRunnerHttpContextExtensions.TriggerBatchWithRequestContextAsync"/> MUST snapshot
/// <see cref="Activity.Current"/> BEFORE awaiting the underlying <see cref="IJobRunner"/> call.
/// The spec's failure mode is "a downstream middleware stops the parent activity" — i.e. the
/// request-scoped Activity is <see cref="Activity.Stop"/>-ed while the trigger task is in flight,
/// flipping <see cref="Activity.Current"/> on the caller's <c>ExecutionContext</c> to a different
/// value before the bridge would have a chance to read it.
///
/// We probe the failure path without depending on the dispatcher's bounded channel or NSubstitute's
/// internal EC fork (which swallows AsyncLocal mutations made inside <c>.Returns(...)</c> lambdas):
/// a hand-rolled <see cref="IJobRunner"/> stub calls <see cref="Activity.Stop"/> on the parent
/// SYNCHRONOUSLY before returning the completed task. The synchronous call runs on the caller's
/// <c>ExecutionContext</c>, so the mutation IS visible to the post-await code path. A correct
/// (snapshot-before-await) bridge captures the parent; a broken (snapshot-after-await) bridge
/// captures whatever <see cref="Activity.Stop"/> left behind.
/// </summary>
[Trait("Category", "Stress")]
public sealed class ActivityPropagationBackpressureTests
{
    private const string ExecutionId = "exec-b2-probe";
    private const string BatchId = "batch-b2-probe";
    private const string ExpectedTraceId = "0123456789abcdef0123456789abcdef";
    private const string ExpectedParentSpanId = "fedcba9876543210";

    [Fact]
    public async Task TriggerWithRequestContext_WhenRunnerStopsParentActivity_CapturesParentBeforeAwait()
    {
        // Arrange: drive a known traceparent so the spawned Activity has a deterministic TraceId.
        using var parent = new Activity("ukbatch.test.parent");
        parent.SetParentId($"00-{ExpectedTraceId}-{ExpectedParentSpanId}-01");
        parent.Start();
        Activity.Current.Should().BeSameAs(parent, "the test depends on Activity.Current == parent at the call site");

        var triggerContext = Substitute.For<IJobTriggerContext>();
        triggerContext.GetTriggeredByOrNull().Returns("test-user");

        var traceContext = Substitute.For<IJobTraceContext>();

        // Hand-rolled stub (NOT NSubstitute) — NSubstitute's.Returns(lambda) invokes the lambda
        // on a forked EC, so AsyncLocal mutations made there don't propagate back to the caller.
        // A direct interface impl keeps the mutation in the caller's EC.
        var runner = new ParentStoppingJobRunner(parent, NewExecution(ExecutionId), batchId: BatchId);

        // Act
        var execution = await runner.TriggerWithRequestContextAsync(
            triggerContext,
            traceContext,
            jobName: "any-job",
            parameters: JobParameters.Empty,
            cancellationToken: CancellationToken.None);

        // Note: ExecutionContext is forked at the await boundary into the bridge's async state
        // machine, so the parent.Stop inside the runner does NOT flow back to this test's EC
        // Activity.Current as observed here is still parent. The probe is on the value the
        // BRIDGE captured (post-await it would see the mutation; pre-await it preserves parent).
        execution.ExecutionId.Should().Be(ExecutionId);
        var captured = (Activity?)traceContext
            .ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IJobTraceContext.CaptureActivity))
            .GetArguments()[1];
        captured.Should().NotBeNull("snapshot-before-await must preserve the ambient Activity across the runner call");
        captured!.TraceId.ToString().Should().Be(
            ExpectedTraceId,
            "the captured Activity must be the original parent — proving snapshot-before-await semantics");
        captured.Should().BeSameAs(parent);
    }

    [Fact]
    public async Task TriggerBatchWithRequestContext_WhenRunnerStopsParentActivity_CapturesParentBeforeAwait()
    {
        // Mirror test for the batch variant — the invariant applies to both overloads.
        using var parent = new Activity("ukbatch.test.parent.batch");
        parent.SetParentId($"00-{ExpectedTraceId}-{ExpectedParentSpanId}-01");
        parent.Start();
        Activity.Current.Should().BeSameAs(parent);

        var triggerContext = Substitute.For<IJobTriggerContext>();
        triggerContext.GetTriggeredByOrNull().Returns("test-user");

        var traceContext = Substitute.For<IJobTraceContext>();

        var runner = new ParentStoppingJobRunner(parent, NewExecution(ExecutionId), batchId: BatchId);

        var returnedBatchId = await runner.TriggerBatchWithRequestContextAsync(
            triggerContext,
            traceContext,
            batchDefinitionId: "any-batch",
            initialParameters: null,
            cancellationToken: CancellationToken.None);

        // See note in the sibling test — Activity.Current as observed here is still parent due to
        // EC fork at the bridge's async boundary. The probe is on the captured value.
        returnedBatchId.Should().Be(BatchId);
        var captured = (Activity?)traceContext
            .ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IJobTraceContext.CaptureActivity))
            .GetArguments()[1];
        captured.Should().NotBeNull();
        captured!.TraceId.ToString().Should().Be(ExpectedTraceId);
        captured.Should().BeSameAs(parent);
    }

    private static JobExecution NewExecution(string executionId) => new()
    {
        ExecutionId = executionId,
        JobName = "any-job",
        Status = JobStatus.Pending,
        Parameters = JobParameters.Empty.Values,
        EnqueuedAtUtc = DateTimeOffset.UtcNow,
        AttemptNumber = 1,
        MaxRetries = 0,
        Processed = 0,
        Failed = 0,
    };

    /// <summary>
    /// Test stub that stops the supplied <see cref="Activity"/> synchronously on the caller's EC
    /// before returning the completed task. Used to simulate the failure mode: a downstream
    /// middleware terminates the request-scoped Activity while the trigger is in flight.
    /// </summary>
    private sealed class ParentStoppingJobRunner : IJobRunner
    {
        private readonly Activity _activityToStop;
        private readonly JobExecution _execution;
        private readonly string _batchId;

        public ParentStoppingJobRunner(Activity activityToStop, JobExecution execution, string batchId)
        {
            _activityToStop = activityToStop;
            _execution = execution;
            _batchId = batchId;
        }

        public Task<JobExecution> TriggerAsync(
            string jobName,
            JobParameters parameters,
            string? triggeredBy,
            CancellationToken cancellationToken)
        {
            _activityToStop.Stop();
            return Task.FromResult(_execution);
        }

        public Task<string> TriggerBatchAsync(
            string batchDefinitionId,
            JobParameters? initialParameters,
            string? triggeredBy,
            CancellationToken cancellationToken)
        {
            _activityToStop.Stop();
            return Task.FromResult(_batchId);
        }

        public Task CancelAsync(string executionId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
