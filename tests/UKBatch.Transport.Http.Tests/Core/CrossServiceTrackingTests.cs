using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Abstractions.Transport;
using UKBatch.AspNetCore;
using UKBatch.Builders;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Transport.Http.Tests.Core;

/// <summary>
/// Core cross-service EXECUTION TRACKING coverage. The orchestrator mints a server-side
/// <see cref="JobExecution"/> shadow row (Running → terminal) for every cross-service batch step so the
/// dashboard reflects work that actually runs on a remote worker.
/// </summary>
/// <remarks>
/// <para>Uses the REAL <see cref="InMemoryJobStore"/> (so the shadow row actually persists AND the
/// <see cref="IJobExecutionWatchHub"/> fans out live) + a substitute <see cref="ITransport"/> whose
/// <c>RequestReplyAsync</c> returns a controlled <see cref="JobResult"/>. Assertions read back through
/// the public <see cref="IJobStore"/> surface (<c>QueryAsync</c> / <c>GetAsync</c>) — this test project
/// is NOT a Core friend, mirroring <c>CrossServiceBatchExecutorTests</c>.</para>
/// <para><b>Deterministic, not time-based:</b> we subscribe to the watch hub BEFORE triggering and
/// await the terminal shadow-row event off the live stream (no <c>Task.Delay</c> polling-then-assert).
/// A hard <see cref="CancellationTokenSource"/> deadline turns any hang into a fast, legible failure.</para>
/// <para><b>★ Headline test:</b> <see cref="WorkerReturnsCancelled_ShadowRow_NormalizedToFailed"/>
/// a worker-reported <c>Cancelled</c> on the normal return path must collapse to <c>Failed</c> in the
/// (Running) shadow row, NOT throw <c>InvalidJobTransitionException</c> nor orphan the row in Running.</para>
/// </remarks>
[Trait("Category", "CrossServiceTracking")]
public sealed class CrossServiceTrackingTests
{
    /// <summary>Generous safety deadline; the happy paths resolve in single-digit ms via the live hub.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    private sealed class NoopJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Boots a real host (in-memory storage + watch hub) with the InProcess transport replaced by a
    /// substitute. Returns the running app, the substitute transport, the public runner/lookup, the
    /// REAL job store, and the shared watch hub.
    /// </summary>
    private static async Task<Harness> BootAsync(
        Action<UKBatchBuilder> configureBuilder,
        ITransport transport,
        string? thisServiceName = "orchestrator-svc")
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddUKBatchAspNetCore(b =>
        {
            b.UseInMemoryStorage();
            if (thisServiceName is not null)
            {
                b.Configure(o => o.ThisServiceName = thisServiceName);
            }
            b.AddJob<NoopJob>();
            configureBuilder(b);
        });
        var existing = builder.Services.FirstOrDefault(d => d.ServiceType == typeof(ITransport));
        if (existing is not null) builder.Services.Remove(existing);
        builder.Services.AddSingleton(transport);

        var host = builder.Build();
        await host.StartAsync();
        return new Harness(
            host,
            transport,
            host.Services.GetRequiredService<IJobRunner>(),
            host.Services.GetRequiredService<IBatchDefinitionLookup>(),
            host.Services.GetRequiredService<IJobStore>(),
            host.Services.GetRequiredService<IJobExecutionWatchHub>());
    }

    private sealed record Harness(
        IHost Host,
        ITransport Transport,
        IJobRunner Runner,
        IBatchDefinitionLookup Lookup,
        IJobStore Store,
        IJobExecutionWatchHub WatchHub);

    /// <summary>A substitute transport whose single cross-service reply is the supplied result.</summary>
    private static ITransport TransportReturning(JobResult result)
    {
        var transport = Substitute.For<ITransport>();
        transport.Name.Returns("Test");
        transport.RequestReplyAsync(Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(result);
        return transport;
    }

    /// <summary>A substitute transport whose cross-service call throws the supplied exception (sync-throw via NSubstitute).</summary>
    private static ITransport TransportThrowing(Func<Exception> exceptionFactory)
    {
        var transport = Substitute.For<ITransport>();
        transport.Name.Returns("Test");
        transport.RequestReplyAsync(Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<JobResult>(_ => throw exceptionFactory());
        return transport;
    }

    private static JobResult Completed(string id = "remote-ok") =>
        new() { ExecutionId = id, Status = JobStatus.Completed, CompletedAtUtc = DateTimeOffset.UtcNow };

    /// <summary>
    /// Live-stream collector: subscribes to the watch hub and records every <see cref="JobExecution"/>
    /// snapshot (in arrival order) for the given batch run. Disposing cancels the subscription. Used to
    /// assert the Running-then-terminal ordering AND as the deterministic "the shadow row reached
    /// terminal" signal.
    /// </summary>
    /// <remarks>
    /// <b>Subscription-before-trigger race (the reason for <see cref="StartAsync"/>):</b> the hub is
    /// LIVE-ONLY (no replay) and its <c>WatchAsync</c> registers the subscription INSIDE the
    /// <c>async IAsyncEnumerable</c> iterator — i.e. only when the consumer issues the first
    /// <c>MoveNextAsync</c>. If we triggered the batch before that, the Running/terminal publishes
    /// would be fanned out to ZERO subscribers and lost (manifested as a flake under parallel CPU load).
    /// <see cref="StartAsync"/> drives the first <c>MoveNextAsync</c> on the pump task and only returns
    /// once the subscription is registered, closing the window deterministically.
    /// </remarks>
    private sealed class WatchCollector : IAsyncDisposable
    {
        private readonly List<JobExecution> _events = new();
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _pump;
        private string? _batchId;

        private WatchCollector(IJobExecutionWatchHub hub, TaskCompletionSource subscribed)
        {
            _pump = Task.Run(async () =>
            {
                // Manual enumeration: the FIRST MoveNextAsync registers the hub subscription (runs the
                // iterator prologue) — signal readiness the instant it is issued, BEFORE awaiting it.
                await using var e = hub.WatchAsync(WatchOptions.Default, _cts.Token).GetAsyncEnumerator(_cts.Token);
                var moveNext = e.MoveNextAsync();
                subscribed.TrySetResult();
                var hasNext = await moveNext.ConfigureAwait(false);
                while (hasNext)
                {
                    var ex = e.Current;
                    lock (_gate)
                    {
                        if (_batchId is null || ex.BatchId == _batchId)
                        {
                            _events.Add(ex);
                        }
                    }
                    hasNext = await e.MoveNextAsync().ConfigureAwait(false);
                }
            });
        }

        /// <summary>
        /// Creates the collector and AWAITS subscription registration (closing the live-only race) before
        /// returning. Callers MUST trigger the batch only after this completes.
        /// </summary>
        public static async Task<WatchCollector> StartAsync(IJobExecutionWatchHub hub)
        {
            var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var collector = new WatchCollector(hub, subscribed);
            await subscribed.Task;
            return collector;
        }

        /// <summary>Filters retained/future events to one batch run once it is known.</summary>
        public void ScopeTo(string batchId)
        {
            lock (_gate)
            {
                _batchId = batchId;
                _events.RemoveAll(e => e.BatchId != batchId);
            }
        }

        public IReadOnlyList<JobExecution> Snapshot()
        {
            lock (_gate) { return _events.ToArray(); }
        }

        /// <summary>The per-exec-id ordered status sequence as seen on the live stream.</summary>
        public IReadOnlyList<JobStatus> StatusSequenceFor(string executionId)
        {
            lock (_gate)
            {
                return _events.Where(e => e.ExecutionId == executionId).Select(e => e.Status).ToArray();
            }
        }

        /// <summary>
        /// Spins (yielding, not sleeping) until <paramref name="predicate"/> over the collected events
        /// holds, or the deadline trips. Returns the matching snapshot. Throws on timeout with the
        /// events seen so far for a legible failure.
        /// </summary>
        public async Task<IReadOnlyList<JobExecution>> WaitUntilAsync(
            Func<IReadOnlyList<JobExecution>, bool> predicate, CancellationToken deadline)
        {
            while (true)
            {
                var snap = Snapshot();
                if (predicate(snap)) return snap;
                if (deadline.IsCancellationRequested)
                {
                    var seen = string.Join(", ", snap.Select(e => $"{e.JobName}:{e.Status}"));
                    throw new TimeoutException($"WatchCollector deadline; events seen: [{seen}]");
                }
                await Task.Yield();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try { await _pump; } catch (OperationCanceledException) { /* expected on cancel */ }
            _cts.Dispose();
        }
    }

    /// <summary>Reads back all stored executions for a batch run (terminal-state read-back assertions).</summary>
    private static async Task<IReadOnlyList<JobExecution>> RowsForBatchAsync(IJobStore store, string batchId) =>
        await store.QueryAsync(new JobQuery { BatchId = batchId, Limit = 100 }, CancellationToken.None);

    // ───────────────────────────────────────────────────────────────────────────────────────────
    // 1. Success → Running-then-Completed shadow row (full field-fidelity).
    // ───────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task RunCrossServiceStep_WorkerCompleted_WritesRunningThenCompletedShadowRow()
    {
        var transport = TransportReturning(Completed());
        var h = await BootAsync(
            b => b.AddBatch("xs-success", c => c.RunJob("RemoteJob", j => j.OnService("billing"))),
            transport);
        using (h.Host)
        await using (var watch = await WatchCollector.StartAsync(h.WatchHub))
        {
            var def = h.Lookup.TryGetByName("xs-success")!;
            var step = def.Steps.Single();
            using var deadline = new CancellationTokenSource(Deadline);

            var batchId = await h.Runner.TriggerBatchAsync(def.Id, JobParameters.Empty, triggeredBy: "alice", CancellationToken.None);
            watch.ScopeTo(batchId);

            var rows = await watch.WaitUntilAsync(
                evts => evts.Any(e => e.Status == JobStatus.Completed), deadline.Token);

            var row = (await RowsForBatchAsync(h.Store, batchId)).Should().ContainSingle().Subject;
            row.Status.Should().Be(JobStatus.Completed);
            row.WorkerName.Should().Be("billing");
            row.BatchStepId.Should().Be(step.StepId);
            row.BatchDefinitionId.Should().Be(def.Id);
            row.BatchId.Should().Be(batchId);
            row.JobName.Should().Be("RemoteJob");
            row.TriggeredBy.Should().Be("alice");
            row.StartedAtUtc.Should().NotBeNull();
            row.CompletedAtUtc.Should().NotBeNull();

            await h.Host.StopAsync();
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────────────
    // 2. Worker returns Failed → row Failed + LastError; batch throws BatchStepFailureException.
    // ───────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task RunCrossServiceStep_WorkerFailed_WritesFailedRowWithLastError()
    {
        var transport = TransportReturning(new JobResult
        {
            ExecutionId = "remote-fail",
            Status = JobStatus.Failed,
            ErrorMessage = "boom",
            CompletedAtUtc = DateTimeOffset.UtcNow,
        });
        var h = await BootAsync(
            b => b.AddBatch("xs-fail", c => c.RunJob("FailingJob", j => j.OnService("billing"))),
            transport);
        using (h.Host)
        await using (var watch = await WatchCollector.StartAsync(h.WatchHub))
        {
            var def = h.Lookup.TryGetByName("xs-fail")!;
            using var deadline = new CancellationTokenSource(Deadline);

            // TriggerBatchAsync returns the run id synchronously; the batch body runs fire-and-forget on
            // the internal run task, where the Failed remote result surfaces a BatchStepFailureException
            // (not awaitable here without a Core friend seam — the shadow-row Failed state is the contract
            // we assert via the public store surface).
            var batchId = await h.Runner.TriggerBatchAsync(def.Id, JobParameters.Empty, triggeredBy: "t", CancellationToken.None);
            watch.ScopeTo(batchId);

            await watch.WaitUntilAsync(evts => evts.Any(e => e.Status == JobStatus.Failed), deadline.Token);

            var row = (await RowsForBatchAsync(h.Store, batchId)).Should().ContainSingle().Subject;
            row.Status.Should().Be(JobStatus.Failed);
            row.LastError.Should().Be("boom");
            row.WorkerName.Should().Be("billing");

            await h.Host.StopAsync();
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────────────
    // 3. Transport throws a GENERIC exception → row Failed (not stuck Running).
    // ───────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task RunCrossServiceStep_TransportThrowsGeneric_WritesFailedRow_NotStuckRunning()
    {
        var transport = TransportThrowing(() => new InvalidOperationException("transport exploded"));
        var h = await BootAsync(
            b => b.AddBatch("xs-throw", c => c.RunJob("RemoteJob", j => j.OnService("billing"))),
            transport);
        using (h.Host)
        await using (var watch = await WatchCollector.StartAsync(h.WatchHub))
        {
            var def = h.Lookup.TryGetByName("xs-throw")!;
            using var deadline = new CancellationTokenSource(Deadline);

            var batchId = await h.Runner.TriggerBatchAsync(def.Id, JobParameters.Empty, triggeredBy: "t", CancellationToken.None);
            watch.ScopeTo(batchId);

            await watch.WaitUntilAsync(evts => evts.Any(e => JobStatusTransitions.IsTerminal(e.Status)), deadline.Token);

            var row = (await RowsForBatchAsync(h.Store, batchId)).Should().ContainSingle().Subject;
            row.Status.Should().Be(JobStatus.Failed);
            row.Status.Should().NotBe(JobStatus.Running);
            row.LastError.Should().Contain("transport exploded");

            await h.Host.StopAsync();
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────────────
    // 4. Transport throws TimeoutException → row Failed.
    // ───────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task RunCrossServiceStep_TransportTimeout_WritesFailedRow()
    {
        var transport = TransportThrowing(() => new TimeoutException("rpc timed out"));
        var h = await BootAsync(
            b => b.AddBatch("xs-timeout", c => c.RunJob("SlowJob", j => j.OnService("billing"))),
            transport);
        using (h.Host)
        await using (var watch = await WatchCollector.StartAsync(h.WatchHub))
        {
            var def = h.Lookup.TryGetByName("xs-timeout")!;
            using var deadline = new CancellationTokenSource(Deadline);

            var batchId = await h.Runner.TriggerBatchAsync(def.Id, JobParameters.Empty, triggeredBy: "t", CancellationToken.None);
            watch.ScopeTo(batchId);

            await watch.WaitUntilAsync(evts => evts.Any(e => JobStatusTransitions.IsTerminal(e.Status)), deadline.Token);

            var row = (await RowsForBatchAsync(h.Store, batchId)).Should().ContainSingle().Subject;
            row.Status.Should().Be(JobStatus.Failed);
            row.LastError.Should().Contain("timed out");

            await h.Host.StopAsync();
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────────────
    // 5. ★ worker legitimately returns Cancelled → shadow row Failed (normalized).
    // The Running shadow row never passed through Cancelling, so Running -> Cancelled is ILLEGAL;
    // RecordCrossServiceEndAsync must collapse it to Failed — NOT throw InvalidJobTransitionException,
    // NOT orphan the row in Running.
    // ───────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task RunCrossServiceStep_WorkerReturnsCancelled_ShadowRowNormalizedToFailed_NoInvalidTransitionLeak()
    {
        var transport = TransportReturning(new JobResult
        {
            ExecutionId = "remote-cancelled",
            Status = JobStatus.Cancelled,   // legit: worker shut down mid-reply and finalized its OWN row Cancelled.
            ErrorMessage = null,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        });
        var h = await BootAsync(
            b => b.AddBatch("xs-cancelled", c => c.RunJob("RemoteJob", j => j.OnService("billing"))),
            transport);
        using (h.Host)
        await using (var watch = await WatchCollector.StartAsync(h.WatchHub))
        {
            var def = h.Lookup.TryGetByName("xs-cancelled")!;
            using var deadline = new CancellationTokenSource(Deadline);

            var batchId = await h.Runner.TriggerBatchAsync(def.Id, JobParameters.Empty, triggeredBy: "t", CancellationToken.None);
            watch.ScopeTo(batchId);

            // The terminal event MUST be Failed (the normalized collapse), and it MUST arrive — if the
            // normalize were missing, UpdateStatusAsync(Cancelled) would throw and no terminal event would publish.
            await watch.WaitUntilAsync(evts => evts.Any(e => JobStatusTransitions.IsTerminal(e.Status)), deadline.Token);

            var row = (await RowsForBatchAsync(h.Store, batchId)).Should().ContainSingle().Subject;
            row.Status.Should().Be(JobStatus.Failed, "Running -> Cancelled is illegal; the worker-Cancelled status must normalize to Failed");
            row.Status.Should().NotBe(JobStatus.Cancelled);
            row.Status.Should().NotBe(JobStatus.Running, "the row must reach a terminal state, never orphan in Running");
            row.LastError.Should().NotBeNull("the normalize arm supplies a default 'cancelled by remote worker' message");

            // No illegal-transition event ever surfaced on the live stream (would mean a Cancelled row was published).
            watch.Snapshot().Should().NotContain(e => e.Status == JobStatus.Cancelled);

            await h.Host.StopAsync();
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────────────
    // 6. Mid-flight cancel via HOST SHUTDOWN: the batch executor runs against the host's
    // ApplicationStopping token (TriggerBatchAsync decouples from the CALLER CT by design).
    // Stopping the host trips that token while the transport is awaiting,
    // producing an OperationCanceledException; the OCE cancel arm must write Failed (Running ->
    // Cancelled is illegal), CT-decoupled so the terminal row lands during shutdown — never stuck
    // Running, no InvalidJobTransitionException.
    // ───────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task RunCrossServiceStep_HostShutdownMidFlight_ShadowRowEndsFailed()
    {
        var crossServiceObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Transport blocks until ITS cancellation token (the batch's host-stopping CT) trips, then
        // surfaces an OCE — simulating an in-flight cross-service call interrupted by host shutdown.
        var transport = Substitute.For<ITransport>();
        transport.Name.Returns("Test");
        transport.RequestReplyAsync(Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var ct = ci.Arg<CancellationToken>();
                crossServiceObserved.TrySetResult();
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await using (ct.Register(() => tcs.TrySetResult()))
                {
                    await tcs.Task;
                }
                ct.ThrowIfCancellationRequested();   // batch CT tripped → OCE into the executor's cancel arm.
                return Completed();
            });

        var h = await BootAsync(
            b => b.AddBatch("xs-host-shutdown", c => c.RunJob("RemoteJob", j => j.OnService("billing"))),
            transport);
        await using (var watch = await WatchCollector.StartAsync(h.WatchHub))
        {
            var def = h.Lookup.TryGetByName("xs-host-shutdown")!;
            using var deadline = new CancellationTokenSource(Deadline);

            var batchId = await h.Runner.TriggerBatchAsync(def.Id, JobParameters.Empty, triggeredBy: "t", CancellationToken.None);
            watch.ScopeTo(batchId);

            // Wait until the transport is actually awaiting (Running shadow row minted), THEN stop the
            // host — tripping ApplicationStopping, which is the batch's CT. StopAsync drains the batch
            // task (the cancel arm's terminal write uses CancellationToken.None, so it lands).
            await crossServiceObserved.Task.WaitAsync(deadline.Token);
            await watch.WaitUntilAsync(evts => evts.Any(e => e.Status == JobStatus.Running), deadline.Token);
            await h.Host.StopAsync(deadline.Token);

            await watch.WaitUntilAsync(evts => evts.Any(e => JobStatusTransitions.IsTerminal(e.Status)), deadline.Token);

            var row = (await RowsForBatchAsync(h.Store, batchId)).Should().ContainSingle().Subject;
            row.Status.Should().Be(JobStatus.Failed, "the OCE cancel arm writes Failed, not Cancelled (Running -> Cancelled is illegal)");
            row.Status.Should().NotBe(JobStatus.Running, "the row must reach a terminal state, never orphan in Running");
            watch.Snapshot().Should().NotContain(e => e.Status == JobStatus.Cancelled);
        }
        h.Host.Dispose();
    }

    // ───────────────────────────────────────────────────────────────────────────────────────────
    // 7. Watch hub emits Running THEN the terminal (live DAG-coloring guarantee).
    // ───────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task RunCrossServiceStep_WatchHub_EmitsRunningThenTerminalForShadowExec()
    {
        var transport = TransportReturning(Completed());
        var h = await BootAsync(
            b => b.AddBatch("xs-watch", c => c.RunJob("RemoteJob", j => j.OnService("billing"))),
            transport);
        using (h.Host)
        await using (var watch = await WatchCollector.StartAsync(h.WatchHub))
        {
            var def = h.Lookup.TryGetByName("xs-watch")!;
            using var deadline = new CancellationTokenSource(Deadline);

            var batchId = await h.Runner.TriggerBatchAsync(def.Id, JobParameters.Empty, triggeredBy: "t", CancellationToken.None);
            watch.ScopeTo(batchId);

            var rows = await watch.WaitUntilAsync(
                evts => evts.Any(e => e.Status == JobStatus.Completed), deadline.Token);

            // Exactly one shadow exec id; its live status sequence is Running THEN Completed (in order).
            var execId = rows.Select(e => e.ExecutionId).Distinct().Should().ContainSingle().Subject;
            var sequence = watch.StatusSequenceFor(execId);
            sequence.Should().StartWith(JobStatus.Running);
            sequence.Should().EndWith(JobStatus.Completed);
            sequence.Should().ContainInOrder(JobStatus.Running, JobStatus.Completed);

            await h.Host.StopAsync();
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────────────
    // 8a. ParallelGroup with 2 cross-service children (distinct services) → 2 shadow rows, each
    // Running → Completed, distinct exec ids, correct WorkerName per child.
    // ───────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task RunParallelGroup_TwoCrossServiceChildren_EachTrackedAsRunningThenCompleted()
    {
        // Per-target reply: both Completed. Routes on the JobMessage.TargetService argument.
        var transport = Substitute.For<ITransport>();
        transport.Name.Returns("Test");
        transport.RequestReplyAsync(Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ci => new JobResult
            {
                ExecutionId = $"remote-{ci.Arg<string>()}",
                Status = JobStatus.Completed,
                CompletedAtUtc = DateTimeOffset.UtcNow,
            });

        var h = await BootAsync(
            b => b.AddBatch("xs-parallel", c => c.ThenInParallel(g => g
                .RunJob("ChildA", j => j.OnService("svc-a"))
                .RunJob("ChildB", j => j.OnService("svc-b")))),
            transport);
        using (h.Host)
        await using (var watch = await WatchCollector.StartAsync(h.WatchHub))
        {
            var def = h.Lookup.TryGetByName("xs-parallel")!;
            using var deadline = new CancellationTokenSource(Deadline);

            var batchId = await h.Runner.TriggerBatchAsync(def.Id, JobParameters.Empty, triggeredBy: "t", CancellationToken.None);
            watch.ScopeTo(batchId);

            await watch.WaitUntilAsync(
                evts => evts.Count(e => e.Status == JobStatus.Completed) >= 2, deadline.Token);

            var rows = await RowsForBatchAsync(h.Store, batchId);
            rows.Should().HaveCount(2);
            rows.Select(r => r.ExecutionId).Distinct().Should().HaveCount(2, "each child gets its own shadow exec id");
            rows.Should().AllSatisfy(r => r.Status.Should().Be(JobStatus.Completed));
            rows.Should().Contain(r => r.JobName == "ChildA" && r.WorkerName == "svc-a");
            rows.Should().Contain(r => r.JobName == "ChildB" && r.WorkerName == "svc-b");

            await h.Host.StopAsync();
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────────────
    // 8b. ParallelGroup mixed-terminal: one child Completed, one Failed → 2 shadow rows reflect each.
    // ───────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task RunParallelGroup_OneChildCompletesOneChildFails_BothShadowRowsReflectTerminal()
    {
        var transport = Substitute.For<ITransport>();
        transport.Name.Returns("Test");
        transport.RequestReplyAsync(Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var target = ci.Arg<string>();
                return target == "svc-bad"
                    ? new JobResult { ExecutionId = "remote-bad", Status = JobStatus.Failed, ErrorMessage = "child blew up", CompletedAtUtc = DateTimeOffset.UtcNow }
                    : new JobResult { ExecutionId = "remote-good", Status = JobStatus.Completed, CompletedAtUtc = DateTimeOffset.UtcNow };
            });

        var h = await BootAsync(
            b => b.AddBatch("xs-parallel-mixed", c => c.ThenInParallel(g => g
                .RunJob("GoodChild", j => j.OnService("svc-good"))
                .RunJob("BadChild", j => j.OnService("svc-bad")))),
            transport);
        using (h.Host)
        await using (var watch = await WatchCollector.StartAsync(h.WatchHub))
        {
            var def = h.Lookup.TryGetByName("xs-parallel-mixed")!;
            using var deadline = new CancellationTokenSource(Deadline);

            var batchId = await h.Runner.TriggerBatchAsync(def.Id, JobParameters.Empty, triggeredBy: "t", CancellationToken.None);
            watch.ScopeTo(batchId);

            // Both children reach a terminal state (WaitAll: the group fails, but BOTH rows finalize).
            await watch.WaitUntilAsync(
                evts => evts.Count(e => JobStatusTransitions.IsTerminal(e.Status)) >= 2, deadline.Token);

            var rows = await RowsForBatchAsync(h.Store, batchId);
            rows.Should().HaveCount(2);
            rows.Should().Contain(r => r.JobName == "GoodChild" && r.WorkerName == "svc-good" && r.Status == JobStatus.Completed);
            var bad = rows.Single(r => r.JobName == "BadChild");
            bad.WorkerName.Should().Be("svc-bad");
            bad.Status.Should().Be(JobStatus.Failed);
            bad.LastError.Should().Be("child blew up");

            await h.Host.StopAsync();
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────────────
    // 9. R4: a LOCAL-only batch writes NO cross-service shadow rows — the rows are exactly the local
    // ones (correct WorkerName == null), guarding the additive boundary. Transport never invoked.
    // ───────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task LocalOnlyBatch_WritesNoCrossServiceShadowRows()
    {
        var transport = Substitute.For<ITransport>();
        transport.Name.Returns("Test");

        var h = await BootAsync(
            b => b.AddBatch("local-only", c => c.RunJob<NoopJob>().ThenRunJob<NoopJob>()),
            transport);
        using (h.Host)
        await using (var watch = await WatchCollector.StartAsync(h.WatchHub))
        {
            var def = h.Lookup.TryGetByName("local-only")!;
            using var deadline = new CancellationTokenSource(Deadline);

            var batchId = await h.Runner.TriggerBatchAsync(def.Id, JobParameters.Empty, triggeredBy: "t", CancellationToken.None);
            watch.ScopeTo(batchId);

            // Both local steps complete; the watch hub fans out their normal-path rows.
            await watch.WaitUntilAsync(
                evts => evts.Count(e => e.Status == JobStatus.Completed) >= 2, deadline.Token);

            var rows = await RowsForBatchAsync(h.Store, batchId);
            rows.Should().HaveCount(2, "two LOCAL steps → exactly two normal-path rows, no extra cross-service shadows");
            rows.Should().AllSatisfy(r =>
            {
                r.JobName.Should().Be(typeof(NoopJob).FullName);
                r.WorkerName.Should().BeNull("local in-process executions have no WorkerName — only cross-service shadows do");
            });

            // The transport was never engaged for a local-only batch (additive boundary intact).
            await transport.DidNotReceiveWithAnyArgs().RequestReplyAsync(default!, default!, default, default);

            await h.Host.StopAsync();
        }
    }
}
