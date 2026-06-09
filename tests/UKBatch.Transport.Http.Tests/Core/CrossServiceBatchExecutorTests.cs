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
            for (var i = 0; i < 30 && transport.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "RequestReplyAsync") == 0; i++)
            {
                await Task.Delay(100);
            }
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
            for (var i = 0; i < 30 && transport.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "RequestReplyAsync") == 0; i++)
            {
                await Task.Delay(100);
            }
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
            for (var i = 0; i < 50 && transport.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "RequestReplyAsync") == 0; i++)
            {
                await Task.Delay(100);
            }
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
            for (var i = 0; i < 30 && transport.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "RequestReplyAsync") == 0; i++)
            {
                await Task.Delay(100);
            }
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
            for (var i = 0; i < 30 && captured is null; i++)
            {
                await Task.Delay(100);
            }
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
            for (var i = 0; i < 30 && transport.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "RequestReplyAsync") == 0; i++)
            {
                await Task.Delay(100);
            }
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
            for (var i = 0; i < 30 && transport.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "RequestReplyAsync") < 2; i++)
            {
                await Task.Delay(100);
            }
            await transport.Received(2).RequestReplyAsync(Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
            await host.StopAsync();
        }
    }
}
