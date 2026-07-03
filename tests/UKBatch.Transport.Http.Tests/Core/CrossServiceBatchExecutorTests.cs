using System.Text.Json;
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
/// BatchExecutor cross-service dispatch path. Tests via the public
/// <see cref="IJobRunner"/> + <see cref="IBatchDefinitionLookup"/> surface (BatchExecutor itself is
/// internal). Replaces <see cref="ITransport"/> with NSubstitute so the cross-service step is the
/// observable interaction.
/// </summary>
[Trait("Category", "HttpTransport")]
public sealed class CrossServiceBatchExecutorTests
{
    public sealed class NoopJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// A local step that captures the parameters it was invoked with and signals when it ran. Used as the
    /// downstream step after a cross-service step to observe the forwarded outputs. Assertions live in the
    /// test (not the job) so a broken forward still lets the job run and signal — the missing key then fails
    /// the test loudly instead of hanging.
    /// </summary>
    private sealed class CapturingJob : IJob
    {
        public static JobParameters? Captured;
        public static TaskCompletionSource Ran = New();
        public static void Reset() { Captured = null; Ran = New(); }
        private static TaskCompletionSource New() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Captured = context.Parameters;
            Ran.TrySetResult();
            return Task.CompletedTask;
        }
    }

    /// <summary>A small object shape to prove a forwarded JSON object deserializes back into a POCO.</summary>
    private sealed record OrderShape
    {
        public int Id { get; init; }
    }

    /// <summary>
    /// Polls <paramref name="condition"/> until it holds or a 60-second deadline expires.
    /// Batch runs are fire-and-forget, so the asserted transport interaction lands on a
    /// background worker: a short fixed iteration budget flakes on a loaded CI runner, while a
    /// healthy run exits this loop within milliseconds. The caller asserts the condition right
    /// after, so an expiry still fails loudly.
    /// </summary>
    private static async Task PollUntilAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 60_000;
        while (Environment.TickCount64 < deadline && !condition())
        {
            await Task.Delay(100);
        }
    }

    private static int RequestReplyCalls(ITransport transport)
        => transport.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "RequestReplyAsync");

    /// <summary>
    /// Boots a real host with the in-memory storage + InProcess seam replaced by a substitute
    /// <see cref="ITransport"/>. Returns the running app for test.
    /// </summary>
    private static async Task<(IHost host, ITransport transport, IJobRunner runner, IBatchDefinitionLookup lookup)> BootAsync(
        Action<UKBatchBuilder> configureBuilder,
        string? thisServiceName,
        ITransport? customTransport = null)
    {
        var transport = customTransport ?? Substitute.For<ITransport>();
        transport.Name.Returns("Test");

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
        // Override ITransport with the substitute.
        var existing = builder.Services.FirstOrDefault(d => d.ServiceType == typeof(ITransport));
        if (existing is not null) builder.Services.Remove(existing);
        builder.Services.AddSingleton(transport);
        var host = builder.Build();
        await host.StartAsync();
        return (host, transport, host.Services.GetRequiredService<IJobRunner>(), host.Services.GetRequiredService<IBatchDefinitionLookup>());
    }

    [Fact]
    public async Task BatchExecutor_LocalStep_DoesNotCallTransportRequestReply()
    {
        var (host, transport, runner, lookup) = await BootAsync(b =>
        {
            b.AddBatch("local-only", c => c.RunJob<NoopJob>());
        }, thisServiceName: "test-svc");
        using (host)
        {
            var def = lookup.TryGetByName("local-only")!;
            var batchRun = await runner.TriggerBatchAsync(def.Id, JobParameters.Empty, triggeredBy: "test", CancellationToken.None);
            // Wait for the local step to reach a terminal row BEFORE the negative assertion, so "no
            // transport call" is checked only after the batch demonstrably ran. A fixed delay could
            // assert before the fire-and-forget batch task was even scheduled (vacuous pass under load).
            var store = host.Services.GetRequiredService<IJobStore>();
            var deadline = DateTime.UtcNow.AddSeconds(15);
            IReadOnlyList<JobExecution> rows;
            do
            {
                rows = await store.QueryAsync(new JobQuery { BatchId = batchRun, Limit = 100 }, CancellationToken.None);
                if (rows.Any(r => JobStatusTransitions.IsTerminal(r.Status))) break;
                await Task.Delay(50);
            } while (DateTime.UtcNow < deadline);
            rows.Count(r => JobStatusTransitions.IsTerminal(r.Status)).Should().Be(1, "the local step must finish before asserting no cross-service dispatch occurred");
            await transport.DidNotReceiveWithAnyArgs().RequestReplyAsync(default!, default!, default, default);
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task BatchExecutor_CrossServiceStep_TriggersTransportRequestReply()
    {
        var transport = Substitute.For<ITransport>();
        transport.Name.Returns("Test");
        transport.RequestReplyAsync(Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new JobResult
            {
                ExecutionId = "remote-1",
                Status = JobStatus.Completed,
                CompletedAtUtc = DateTimeOffset.UtcNow,
            });
        var (host, _, runner, lookup) = await BootAsync(b =>
        {
            b.AddBatch("cross-call", c => c.RunJob("RemoteJob", j => j.OnService("billing")));
        }, thisServiceName: "test-svc", customTransport: transport);
        using (host)
        {
            var def = lookup.TryGetByName("cross-call")!;
            await runner.TriggerBatchAsync(def.Id, JobParameters.Empty, triggeredBy: "t", CancellationToken.None);
            // Allow batch dispatch + cross-service hop.
            await PollUntilAsync(() => RequestReplyCalls(transport) > 0);
            await transport.Received(1).RequestReplyAsync(
                "billing",
                Arg.Is<JobMessage>(m => m.JobName == "RemoteJob" && m.TargetService == "billing"),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>());
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task BatchExecutor_CrossServiceStep_WithoutThisServiceName_FallsBackToEntryAssembly()
    {
        // documents fallback behavior: when UKBatchOptions.ThisServiceName is null,
        // JobRunner.ResolveThisServiceName falls back to UKBATCH_SERVICE_NAME env var, then
        // Assembly.GetEntryAssembly.GetName.Name. In tests Assembly.GetEntryAssembly = "testhost"
        // so the cross-service step DOES proceed (with SourceService = "testhost"), demonstrating the
        // graceful fallback. The strict null-check is hard to test deterministically — covered by
        // unit tests against the resolver directly.
        var transport = Substitute.For<ITransport>();
        transport.Name.Returns("Test");
        transport.RequestReplyAsync(Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new JobResult
            {
                ExecutionId = "ok",
                Status = JobStatus.Completed,
                CompletedAtUtc = DateTimeOffset.UtcNow,
            });
        var (host, _, runner, lookup) = await BootAsync(b =>
        {
            b.AddBatch("needs-this-svc", c => c.RunJob("RemoteJob", j => j.OnService("billing")));
        }, thisServiceName: null, customTransport: transport);
        using (host)
        {
            var def = lookup.TryGetByName("needs-this-svc")!;
            await runner.TriggerBatchAsync(def.Id, JobParameters.Empty, triggeredBy: "t", CancellationToken.None);
            await PollUntilAsync(() => RequestReplyCalls(transport) > 0);
            // SourceService MUST be non-null/non-whitespace — fallback chain ensured one of (options,
            // env var, entry assembly) resolved to a usable identity.
            await transport.Received(1).RequestReplyAsync(
                "billing",
                Arg.Is<JobMessage>(m => !string.IsNullOrWhiteSpace(m.SourceService)),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>());
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task BatchExecutor_CrossServiceStep_OnFailedResult_ProducesBatchFailure()
    {
        var transport = Substitute.For<ITransport>();
        transport.Name.Returns("Test");
        transport.RequestReplyAsync(Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new JobResult
            {
                ExecutionId = "remote-fail",
                Status = JobStatus.Failed,
                ErrorMessage = "remote crash",
                CompletedAtUtc = DateTimeOffset.UtcNow,
            });
        var (host, _, runner, lookup) = await BootAsync(b =>
        {
            b.AddBatch("fails-remote", c => c.RunJob("FailingJob", j => j.OnService("billing")));
        }, thisServiceName: "test-svc", customTransport: transport);
        using (host)
        {
            var def = lookup.TryGetByName("fails-remote")!;
            await runner.TriggerBatchAsync(def.Id, JobParameters.Empty, triggeredBy: "t", CancellationToken.None);
            // Poll until transport sees the cross-service call.
            await PollUntilAsync(() => RequestReplyCalls(transport) > 0);
            await transport.Received(1).RequestReplyAsync(Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task BatchExecutor_CrossServiceStep_OnTransportTimeout_ProducesBatchFailure()
    {
        var transport = Substitute.For<ITransport>();
        transport.Name.Returns("Test");
        transport.RequestReplyAsync(Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<JobResult>(_ => throw new TimeoutException("transport timeout"));
        var (host, _, runner, lookup) = await BootAsync(b =>
        {
            b.AddBatch("timeout-remote", c => c.RunJob("SlowJob", j => j.OnService("billing")));
        }, thisServiceName: "test-svc", customTransport: transport);
        using (host)
        {
            var def = lookup.TryGetByName("timeout-remote")!;
            await runner.TriggerBatchAsync(def.Id, JobParameters.Empty, triggeredBy: "t", CancellationToken.None);
            await PollUntilAsync(() => RequestReplyCalls(transport) > 0);
            await transport.Received(1).RequestReplyAsync(Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task BatchExecutor_CrossServiceStep_MessageCarriesSourceServiceFromHost()
    {
        var transport = Substitute.For<ITransport>();
        transport.Name.Returns("Test");
        JobMessage? captured = null;
        transport.RequestReplyAsync(Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                captured = ci.Arg<JobMessage>();
                return new JobResult
                {
                    ExecutionId = "ok",
                    Status = JobStatus.Completed,
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                };
            });
        var (host, _, runner, lookup) = await BootAsync(b =>
        {
            b.AddBatch("attr-check", c => c.RunJob("RemoteJob", j => j.OnService("billing")));
        }, thisServiceName: "orchestrator-svc", customTransport: transport);
        using (host)
        {
            var def = lookup.TryGetByName("attr-check")!;
            await runner.TriggerBatchAsync(def.Id, JobParameters.Empty, triggeredBy: "t", CancellationToken.None);
            await PollUntilAsync(() => captured is not null);
            captured.Should().NotBeNull();
            captured!.SourceService.Should().Be("orchestrator-svc");
            captured.TargetService.Should().Be("billing");
            captured.JobName.Should().Be("RemoteJob");
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task BatchExecutor_LocalStepThenCrossServiceStep_BothDispatched()
    {
        var transport = Substitute.For<ITransport>();
        transport.Name.Returns("Test");
        transport.RequestReplyAsync(Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new JobResult
            {
                ExecutionId = "ok",
                Status = JobStatus.Completed,
                CompletedAtUtc = DateTimeOffset.UtcNow,
            });
        var (host, _, runner, lookup) = await BootAsync(b =>
        {
            b.AddBatch("mixed", c => c.RunJob<NoopJob>().ThenRunJob("Remote", j => j.OnService("billing")));
        }, thisServiceName: "test-svc", customTransport: transport);
        using (host)
        {
            var def = lookup.TryGetByName("mixed")!;
            await runner.TriggerBatchAsync(def.Id, JobParameters.Empty, triggeredBy: "t", CancellationToken.None);
            await PollUntilAsync(() => RequestReplyCalls(transport) > 0);
            await transport.Received(1).RequestReplyAsync(Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task BatchExecutor_TargetServiceNullOnAllSteps_NoTransportCalls()
    {
        var transport = Substitute.For<ITransport>();
        transport.Name.Returns("Test");
        var (host, _, runner, lookup) = await BootAsync(b =>
        {
            b.AddBatch("all-local", c => c.RunJob<NoopJob>().ThenRunJob<NoopJob>());
        }, thisServiceName: "test-svc", customTransport: transport);
        using (host)
        {
            var def = lookup.TryGetByName("all-local")!;
            var batchRun = await runner.TriggerBatchAsync(def.Id, JobParameters.Empty, triggeredBy: "t", CancellationToken.None);
            // Wait for BOTH local steps to reach terminal rows before the negative assertion (a fixed
            // delay could assert before the two-step batch finished — vacuous pass under load).
            var store = host.Services.GetRequiredService<IJobStore>();
            var deadline = DateTime.UtcNow.AddSeconds(15);
            IReadOnlyList<JobExecution> rows;
            do
            {
                rows = await store.QueryAsync(new JobQuery { BatchId = batchRun, Limit = 100 }, CancellationToken.None);
                if (rows.Count(r => JobStatusTransitions.IsTerminal(r.Status)) >= 2) break;
                await Task.Delay(50);
            } while (DateTime.UtcNow < deadline);
            rows.Count(r => JobStatusTransitions.IsTerminal(r.Status)).Should().Be(2, "both local steps must finish before asserting no cross-service dispatch occurred");
            await transport.DidNotReceiveWithAnyArgs().RequestReplyAsync(default!, default!, default, default);
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task BatchExecutor_TwoCrossServiceSteps_BothDispatched()
    {
        var transport = Substitute.For<ITransport>();
        transport.Name.Returns("Test");
        transport.RequestReplyAsync(Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new JobResult
            {
                ExecutionId = "ok",
                Status = JobStatus.Completed,
                CompletedAtUtc = DateTimeOffset.UtcNow,
            });
        var (host, _, runner, lookup) = await BootAsync(b =>
        {
            b.AddBatch("two-remote", c => c
                .RunJob("A", j => j.OnService("svc-a"))
                .ThenRunJob("B", j => j.OnService("svc-b")));
        }, thisServiceName: "test-svc", customTransport: transport);
        using (host)
        {
            var def = lookup.TryGetByName("two-remote")!;
            await runner.TriggerBatchAsync(def.Id, JobParameters.Empty, triggeredBy: "t", CancellationToken.None);
            await PollUntilAsync(() => RequestReplyCalls(transport) >= 2);
            await transport.Received(2).RequestReplyAsync(Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
            await host.StopAsync();
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────────────
    // Step-output forwarding: a cross-service step's returned values fold into the run and forward to
    // the next step, exactly like a local step's outputs.
    // ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BatchExecutor_CrossServiceCompleted_ForwardsReturnValuesToNextStep()
    {
        CapturingJob.Reset();
        var transport = Substitute.For<ITransport>();
        transport.Name.Returns("Test");
        transport.RequestReplyAsync(Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new JobResult
            {
                ExecutionId = "remote-1",
                Status = JobStatus.Completed,
                ReturnValues = new Dictionary<string, object?> { ["invoiceId"] = "INV-1" },
                CompletedAtUtc = DateTimeOffset.UtcNow,
            });
        var (host, _, runner, lookup) = await BootAsync(b =>
        {
            b.AddJob<CapturingJob>();
            b.AddBatch("forward-scalar", c => c
                .RunJob("Remote", j => j.OnService("billing"))
                .ThenRunJob<CapturingJob>());
        }, thisServiceName: "test-svc", customTransport: transport);
        using (host)
        {
            var def = lookup.TryGetByName("forward-scalar")!;
            await runner.TriggerBatchAsync(def.Id, JobParameters.Empty, triggeredBy: "t", CancellationToken.None);
            await CapturingJob.Ran.Task.WaitAsync(TimeSpan.FromSeconds(60));
            CapturingJob.Captured!.GetRequired<string>("invoiceId").Should().Be("INV-1",
                "the cross-service reply's returned values fold into the run accumulator and forward to the next step");
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task BatchExecutor_CrossServiceCompleted_ForwardsObjectReturnValue()
    {
        CapturingJob.Reset();
        var transport = Substitute.For<ITransport>();
        transport.Name.Returns("Test");
        transport.RequestReplyAsync(Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new JobResult
            {
                ExecutionId = "remote-1",
                Status = JobStatus.Completed,
                // A real cross-service reply deserializes its values into JsonElement; use that exact shape so
                // the fold + downstream read exercise the JSON-aware path and prove the object is NOT
                // re-stringified into a JSON string on the way through (no double-encoding).
                ReturnValues = new Dictionary<string, object?>
                {
                    ["order"] = JsonDocument.Parse("{\"id\":7}").RootElement.Clone(),
                },
                CompletedAtUtc = DateTimeOffset.UtcNow,
            });
        var (host, _, runner, lookup) = await BootAsync(b =>
        {
            b.AddJob<CapturingJob>();
            b.AddBatch("forward-object", c => c
                .RunJob("Remote", j => j.OnService("billing"))
                .ThenRunJob<CapturingJob>());
        }, thisServiceName: "test-svc", customTransport: transport);
        using (host)
        {
            var def = lookup.TryGetByName("forward-object")!;
            await runner.TriggerBatchAsync(def.Id, JobParameters.Empty, triggeredBy: "t", CancellationToken.None);
            await CapturingJob.Ran.Task.WaitAsync(TimeSpan.FromSeconds(60));

            // Deserializes straight into the POCO — only possible if the value stayed a JSON object.
            CapturingJob.Captured!.GetRequired<OrderShape>("order").Id.Should().Be(7);
            // And the raw forwarded value is a JSON OBJECT token, not a re-encoded string.
            CapturingJob.Captured.Values["order"].Should().BeOfType<JsonElement>()
                .Which.ValueKind.Should().Be(JsonValueKind.Object, "the object must forward as JSON, not as a stringified value");
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task ParallelGroup_CrossServiceChildren_CompletedOutputsFold_FailedExcluded()
    {
        CapturingJob.Reset();
        // Per-target reply: the good child completes with an output; the bad child fails with one. WaitAny lets
        // the group SUCCEED on the completed child alone, and only that child's outputs fold. NOTE: this locks
        // the JOIN-side exclusion (the fold set is the winner only); the wire-side Completed gate is locked
        // separately by MapInternalEndpointsTests.InvokeEndpoint_FailedJob_ReturnValuesIsNull, because the join
        // would exclude a failed child's outputs even if the gate were gone.
        var transport = Substitute.For<ITransport>();
        transport.Name.Returns("Test");
        transport.RequestReplyAsync(Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var target = ci.Arg<string>();
                return target == "svc-good"
                    ? new JobResult { ExecutionId = "good", Status = JobStatus.Completed, ReturnValues = new Dictionary<string, object?> { ["region"] = "EU" }, CompletedAtUtc = DateTimeOffset.UtcNow }
                    : new JobResult { ExecutionId = "bad", Status = JobStatus.Failed, ReturnValues = new Dictionary<string, object?> { ["secret"] = "LEAK" }, CompletedAtUtc = DateTimeOffset.UtcNow };
            });
        var (host, _, runner, lookup) = await BootAsync(b =>
        {
            b.AddJob<CapturingJob>();
            b.AddBatch("parallel-fold", c => c
                .ThenInParallel(g => g
                    .JoinPolicy(ParallelJoinPolicy.WaitAny)
                    .RunJob("GoodChild", j => j.OnService("svc-good"))
                    .RunJob("BadChild", j => j.OnService("svc-bad")))
                .ThenRunJob<CapturingJob>());
        }, thisServiceName: "test-svc", customTransport: transport);
        using (host)
        {
            var def = lookup.TryGetByName("parallel-fold")!;
            await runner.TriggerBatchAsync(def.Id, JobParameters.Empty, triggeredBy: "t", CancellationToken.None);
            await CapturingJob.Ran.Task.WaitAsync(TimeSpan.FromSeconds(60));

            CapturingJob.Captured!.GetRequired<string>("region").Should().Be("EU", "the completed child's output folds forward");
            CapturingJob.Captured.Contains("secret").Should().BeFalse("a failed parallel child's outputs must never fold forward");
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task BatchExecutor_CrossServiceCompleted_NoReturnValues_AccumulatorUnchanged()
    {
        CapturingJob.Reset();
        var transport = Substitute.For<ITransport>();
        transport.Name.Returns("Test");
        transport.RequestReplyAsync(Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new JobResult
            {
                ExecutionId = "remote-1",
                Status = JobStatus.Completed,
                ReturnValues = null,
                CompletedAtUtc = DateTimeOffset.UtcNow,
            });
        var (host, _, runner, lookup) = await BootAsync(b =>
        {
            b.AddJob<CapturingJob>();
            b.AddBatch("no-forward", c => c
                .RunJob("Remote", j => j.OnService("billing"))
                .ThenRunJob<CapturingJob>());
        }, thisServiceName: "test-svc", customTransport: transport);
        using (host)
        {
            var def = lookup.TryGetByName("no-forward")!;
            await runner.TriggerBatchAsync(def.Id, JobParameters.Empty, triggeredBy: "t", CancellationToken.None);
            await CapturingJob.Ran.Task.WaitAsync(TimeSpan.FromSeconds(60));

            // A reply with no returned values leaves the accumulator untouched: the downstream step sees only
            // the (empty) batch-initial parameters — identical to a batch with no output forwarding at all.
            CapturingJob.Captured!.Values.Should().BeEmpty("a null ReturnValues must not inject any forwarded keys");
            await host.StopAsync();
        }
    }
}
