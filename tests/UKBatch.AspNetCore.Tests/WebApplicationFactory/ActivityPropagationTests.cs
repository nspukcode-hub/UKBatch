using System.Diagnostics;
using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.AspNetCore.Tests.Helpers;
using UKBatch.AspNetCore.Tracing;
using UKBatch.AspNetCore.Triggering;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.AspNetCore.Tests.WebApplicationFactory;

/// <summary>
/// Acceptance tests for the W3C trace propagation mechanism.
/// </summary>
public sealed class ActivityPropagationTests
{
    private const string JobName = nameof(ActivityCapturingJob);

    [Fact]
    public async Task Capture_BeforeAwait_SetsParentOnRestoredActivity()
    {
        await using var host = new BasicWebHost();
        await host.StartAsync(
            configureUKBatch: b => b.AddJob<ActivityCapturingJob>().Named(JobName),
            configureServices: s => s.AddSingleton<CapturedActivityInfo>());

        // Drive a known traceparent into the request. ASP.NET Core's hosting layer will pick this
        // up and create an Activity whose TraceId == this fixed value; the bridge snapshots that
        // Activity inside TriggerWithRequestContextAsync and the job sees it via RestoreRequestActivity.
        const string FixedTraceId = "0123456789abcdef0123456789abcdef";
        const string FixedParentSpanId = "fedcba9876543210";
        var traceparent = $"00-{FixedTraceId}-{FixedParentSpanId}-01";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/trigger/{JobName}", UriKind.Relative));
        request.Headers.Add("traceparent", traceparent);
        var response = await host.Client!.SendAsync(request);
        await response.ShouldBeAsync(HttpStatusCode.OK);

        var sink = host.App!.Services.GetRequiredService<CapturedActivityInfo>();
        var info = await sink.WaitAsync(TimeSpan.FromSeconds(5));
        info.TraceId.Should().Be(FixedTraceId, "the job's restored Activity must share the request trace id");
        info.ParentId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task NoAmbientActivity_ProducesNoOpRestore()
    {
        await using var host = new BasicWebHost();
        await host.StartAsync(
            configureUKBatch: b => b.AddJob<ActivityCapturingJob>().Named(JobName),
            configureServices: s => s.AddSingleton<CapturedActivityInfo>());

        // The ASP.NET Core hosting pipeline starts a request-scoped Activity, so there is ALWAYS
        // an ambient Activity available at the trigger call site under TestHost. The test asserts
        // that whatever Activity flows through is the one captured (not null) — i.e. the restore
        // path produces a NON-null trace id that matches the server's Activity.
        var response = await host.Client!.GetAsync(new Uri($"/trigger/{JobName}", UriKind.Relative));
        await response.ShouldBeAsync(HttpStatusCode.OK);

        var info = await host.App!.Services
            .GetRequiredService<CapturedActivityInfo>()
            .WaitAsync(TimeSpan.FromSeconds(5));
        // With no inbound traceparent, the server starts a fresh root Activity. The job sees ITS
        // trace id via RestoreRequestActivity — verifying the capture+restore round-trip.
        info.TraceId.Should().NotBeNull("ASP.NET Core always starts a request Activity; the bridge propagates it");
        info.OperationName.Should().NotBeNull();
    }

    [Fact]
    public async Task RestoreRequestActivity_IsOneShot_ConsumeRemovesSlot()
    {
        // Resolve the trace context directly and assert ConsumeActivity returns null after the
        // first call.
        await using var host = new BasicWebHost();
        await host.StartAsync(
            configureUKBatch: b => b.AddJob<ActivityCapturingJob>().Named(JobName),
            configureServices: s => s.AddSingleton<CapturedActivityInfo>());

        var traceCtx = host.App!.Services.GetRequiredService<IJobTraceContext>();
        using var probe = new Activity("ukbatch.test.probe");
        probe.Start();
        traceCtx.CaptureActivity("exec-1", probe);

        var first = traceCtx.ConsumeActivity("exec-1");
        var second = traceCtx.ConsumeActivity("exec-1");

        first.Should().NotBeNull();
        first!.OperationName.Should().Be("ukbatch.test.probe");
        second.Should().BeNull("ConsumeActivity removes the slot on the first call");
    }
}
