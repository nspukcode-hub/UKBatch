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
/// A cross-service step inside a parallel group routes through the shared cross-service invoker on its
/// "return the status to the join" branch (distinct from the sequential "throw on failure" branch).
/// The transport is exercised for the parallel child, and a worker-Failed child surfaces as a batch
/// failure with the child's shadow row reaching a terminal state (never stuck Running).
/// </summary>
[Trait("Category", "HttpTransport")]
public sealed class ParallelCrossServiceStepTests
{
    private sealed class NoopJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static async Task PollUntilAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 60_000;
        while (Environment.TickCount64 < deadline && !condition())
        {
            await Task.Delay(50);
        }
    }

    private static int RequestReplyCalls(ITransport transport)
        => transport.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "RequestReplyAsync");

    private static async Task<(IHost Host, ITransport Transport, IJobRunner Runner, IBatchDefinitionLookup Lookup, IJobStore Store)> BootAsync(
        Action<UKBatchBuilder> configureBuilder, ITransport transport)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddUKBatchAspNetCore(b =>
        {
            b.UseInMemoryStorage();
            b.Configure(o => o.ThisServiceName = "orchestrator-svc");
            b.AddJob<NoopJob>();
            configureBuilder(b);
        });
        var existing = builder.Services.FirstOrDefault(d => d.ServiceType == typeof(ITransport));
        if (existing is not null) builder.Services.Remove(existing);
        builder.Services.AddSingleton(transport);
        var host = builder.Build();
        await host.StartAsync();
        return (host, transport,
            host.Services.GetRequiredService<IJobRunner>(),
            host.Services.GetRequiredService<IBatchDefinitionLookup>(),
            host.Services.GetRequiredService<IJobStore>());
    }

    [Fact]
    public async Task ParallelGroup_CrossServiceChild_InvokesTransport()
    {
        var transport = Substitute.For<ITransport>();
        transport.Name.Returns("Test");
        transport.RequestReplyAsync(Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new JobResult { ExecutionId = "remote-ok", Status = JobStatus.Completed, CompletedAtUtc = DateTimeOffset.UtcNow });

        var (host, _, runner, lookup, _) = await BootAsync(
            b => b.AddBatch("xs-parallel-ok", c => c.ThenInParallel(g => g
                .RunJob<NoopJob>()
                .RunJob("RemoteJob", j => j.OnService("billing")))),
            transport);
        try
        {
            var def = lookup.TryGetByName("xs-parallel-ok")!;
            await runner.TriggerBatchAsync(def.Id, null, "test", default);

            await PollUntilAsync(() => RequestReplyCalls(transport) >= 1);
            RequestReplyCalls(transport).Should().BeGreaterThan(0,
                "the parallel cross-service child must dispatch through the transport's RequestReplyAsync.");
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task ParallelGroup_CrossServiceChildFails_ChildShadowRowIsTerminal_NotStuckRunning()
    {
        var transport = Substitute.For<ITransport>();
        transport.Name.Returns("Test");
        transport.RequestReplyAsync(Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new JobResult { ExecutionId = "remote-fail", Status = JobStatus.Failed, ErrorMessage = "boom", CompletedAtUtc = DateTimeOffset.UtcNow });

        var (host, _, runner, lookup, store) = await BootAsync(
            b => b.AddBatch("xs-parallel-fail", c => c.ThenInParallel(g => g
                .RunJob<NoopJob>()
                .RunJob("RemoteJob", j => j.OnService("billing")))),
            transport);
        try
        {
            var def = lookup.TryGetByName("xs-parallel-fail")!;
            await runner.TriggerBatchAsync(def.Id, null, "test", default);

            // The cross-service child writes a shadow row; under a worker-Failed result the parallel
            // branch returns Failed to the join, and the row must end terminal (never stuck Running).
            JobExecution? remoteRow = null;
            await PollUntilAsync(() =>
            {
                var rows = store.QueryAsync(new JobQuery { JobName = "RemoteJob" }, default).GetAwaiter().GetResult();
                remoteRow = rows.FirstOrDefault();
                return remoteRow is not null && (remoteRow.Status is JobStatus.Failed or JobStatus.Completed or JobStatus.Cancelled);
            });
            remoteRow.Should().NotBeNull("the cross-service parallel child writes a shadow row.");
            remoteRow!.Status.Should().Be(JobStatus.Failed, "a worker-Failed result lands a terminal Failed shadow row.");
            remoteRow.Status.Should().NotBe(JobStatus.Running, "the shadow row must never be stuck Running.");
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(10));
        }
    }
}
