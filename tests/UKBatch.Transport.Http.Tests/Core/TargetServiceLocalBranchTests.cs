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
/// A step whose TargetService is whitespace (or empty) must run LOCALLY — the local-vs-cross-service
/// branch guards with <c>string.IsNullOrWhiteSpace</c>, not a bare null check. The wizard normalises
/// blank target services to null, but a raw API or code-defined caller can leave a "   ", and that
/// must never be dispatched to an empty service name.
/// </summary>
[Trait("Category", "HttpTransport")]
public sealed class TargetServiceLocalBranchTests
{
    public sealed class LocalNoopJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task WhitespaceTargetService_RunsLocally_NoTransportDispatch()
    {
        var transport = Substitute.For<ITransport>();
        transport.Name.Returns("Test");

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddUKBatchAspNetCore(b =>
        {
            b.UseInMemoryStorage();
            b.Configure(o => o.ThisServiceName = "test-svc");
            b.AddJob<LocalNoopJob>();
            // Whitespace TargetService — must be treated as local (run here), not dispatched.
            b.AddBatch("ws-local", c => c.RunJob<LocalNoopJob>(j => j.OnService("   ")));
        });
        var existing = builder.Services.FirstOrDefault(d => d.ServiceType == typeof(ITransport));
        if (existing is not null) builder.Services.Remove(existing);
        builder.Services.AddSingleton(transport);

        using var host = builder.Build();
        await host.StartAsync();

        var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
        var runner = host.Services.GetRequiredService<IJobRunner>();
        var store = host.Services.GetRequiredService<IJobStore>();
        var def = lookup.TryGetByName("ws-local")!;

        var batchRun = await runner.TriggerBatchAsync(def.Id, JobParameters.Empty, triggeredBy: "test", CancellationToken.None);

        // Wait for the step to reach a terminal row before the negative assertion, so "no transport
        // dispatch" is checked only after the local step demonstrably ran.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        IReadOnlyList<JobExecution> rows;
        do
        {
            rows = await store.QueryAsync(new JobQuery { BatchId = batchRun, Limit = 100 }, CancellationToken.None);
            if (rows.Any(r => JobStatusTransitions.IsTerminal(r.Status)))
            {
                break;
            }
            await Task.Delay(50);
        } while (DateTime.UtcNow < deadline);

        rows.Count(r => JobStatusTransitions.IsTerminal(r.Status)).Should().Be(1, "a whitespace TargetService is local and must run here");
        await transport.DidNotReceiveWithAnyArgs().RequestReplyAsync(default!, default!, default, default);
        await host.StopAsync();
    }
}
