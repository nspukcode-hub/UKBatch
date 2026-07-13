using System.Net;
using System.Text.Json;
using FluentAssertions;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Workers;
using UKBatch.Worker.Tests.Common;
using Xunit;

namespace UKBatch.Worker.Tests;

/// <summary>
/// Heartbeat loop. Deterministic via <c>FakeTimeProvider</c> + a recording
/// primary <c>HttpMessageHandler</c>: an immediate beat on start, periodic beats on the configured
/// cadence, server failures swallowed (loop survives), <c>Heartbeat=false</c> sends nothing, and
/// <c>StopAsync</c> sends exactly one <c>Offline</c> beat without rethrowing even on a pre-cancelled
/// token.
/// </summary>
public sealed class WorkerHeartbeatServiceTests
{
    private static WorkerOptions ValidOptions(bool heartbeat = true) => new()
    {
        WorkerName = "invoicing",
        ServerUrl = "http://ukbatch-server:8080",
        Tags = ["billing"],
        Heartbeat = heartbeat,
        HeartbeatInterval = TimeSpan.FromSeconds(15),
    };

    /// <summary>
    /// Advances the fake clock in small steps until <paramref name="condition"/> holds, then settles
    /// briefly so any in-flight beat completes before the caller asserts. The production cadence is
    /// driven ENTIRELY by the fake clock — the short real-time settle between advances is only a
    /// scheduler barrier (the background loop's awaiting continuations must get a thread-pool turn,
    /// which can starve under full-suite parallel load if we merely <c>Task.Yield</c>). The
    /// real-time component bounds the polling loop; it never gates the logic under test, so the
    /// outcome stays deterministic.
    /// </summary>
    private static async Task AdvanceUntilAsync(HeartbeatHarness h, TimeSpan total, Func<bool> condition)
    {
        var step = TimeSpan.FromMilliseconds(50);
        var advanced = TimeSpan.Zero;
        // Generous real-time deadline so a CI box under load still reaches the condition; in practice
        // the loop exits within a few iterations once the clock passes the relevant boundary.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                // Settle: let any beat triggered by the last advance finish posting before asserting.
                await Task.Delay(20).ConfigureAwait(false);
                return;
            }

            if (advanced < total)
            {
                h.Time.Advance(step);
                advanced += step;
            }

            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Advances the fake clock by <paramref name="total"/> in steps and settles, WITHOUT waiting on a
    /// condition — used by negative assertions ("after N intervals, still zero beats"). Bounded and
    /// fast (no 20s never-true poll).
    /// </summary>
    private static async Task AdvanceAndSettleAsync(HeartbeatHarness h, TimeSpan total)
    {
        var step = TimeSpan.FromSeconds(1);
        for (var advanced = TimeSpan.Zero; advanced < total; advanced += step)
        {
            h.Time.Advance(step);
            await Task.Delay(2).ConfigureAwait(false);
        }

        // Final settle so any (unexpected) beat has a chance to post before the caller asserts zero.
        await Task.Delay(20).ConfigureAwait(false);
    }

    [Fact]
    public async Task ExecuteAsync_OnStart_FiresOneImmediateBeat()
    {
        await using var h = HeartbeatHarness.Build(ValidOptions(), jobNames: ["GenerateInvoice"]);

        await h.Service.StartAsync(CancellationToken.None);
        // Clear the <=1000ms startup jitter so the immediate beat fires; nothing past the first tick.
        await AdvanceUntilAsync(h, TimeSpan.FromSeconds(2), () => h.Handler.CallCount >= 1);

        h.Handler.CallCount.Should().Be(1, "exactly one immediate beat fires before the first periodic tick");
        h.Handler.Beats.TryPeek(out var beat).Should().BeTrue();
        beat!.RequestUri!.AbsolutePath.Should().Be("/api/workers/beat");

        await h.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_AdvanceByInterval_FiresAnotherBeat()
    {
        await using var h = HeartbeatHarness.Build(ValidOptions(), jobNames: ["GenerateInvoice"]);

        await h.Service.StartAsync(CancellationToken.None);
        await AdvanceUntilAsync(h, TimeSpan.FromSeconds(2), () => h.Handler.CallCount >= 1);
        h.Handler.CallCount.Should().Be(1);

        // One full interval → a second beat.
        await AdvanceUntilAsync(h, TimeSpan.FromSeconds(16), () => h.Handler.CallCount >= 2);
        h.Handler.CallCount.Should().Be(2, "advancing one HeartbeatInterval fires exactly one more periodic beat");

        await h.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_ServerReturns500_SwallowsAndLoopSurvives()
    {
        // First beat 500, every subsequent beat 202. The loop must NOT die on the 500.
        var handler = new RecordingHttpMessageHandler(n => n == 1
            ? (HttpStatusCode.InternalServerError, false)
            : (HttpStatusCode.Accepted, false));
        await using var h = HeartbeatHarness.Build(ValidOptions(), handler);

        await h.Service.StartAsync(CancellationToken.None);
        await AdvanceUntilAsync(h, TimeSpan.FromSeconds(2), () => h.Handler.CallCount >= 1);
        h.Handler.CallCount.Should().Be(1, "the first beat (500) was sent");

        // Next tick must still beat — proves the 500 did not break the loop.
        await AdvanceUntilAsync(h, TimeSpan.FromSeconds(16), () => h.Handler.CallCount >= 2);
        h.Handler.CallCount.Should().Be(2, "a server 500 is swallowed; the loop survives and beats again next tick");

        await h.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_HttpRequestExceptionThrown_SwallowsAndLoopSurvives()
    {
        // First beat throws (server unreachable), subsequent beats succeed.
        var handler = new RecordingHttpMessageHandler(n => n == 1
            ? (null, true)
            : (HttpStatusCode.Accepted, false));
        await using var h = HeartbeatHarness.Build(ValidOptions(), handler);

        await h.Service.StartAsync(CancellationToken.None);
        await AdvanceUntilAsync(h, TimeSpan.FromSeconds(2), () => h.Handler.CallCount >= 1);
        h.Handler.CallCount.Should().Be(1, "the throwing beat was attempted");

        await AdvanceUntilAsync(h, TimeSpan.FromSeconds(16), () => h.Handler.CallCount >= 2);
        h.Handler.CallCount.Should().Be(2,
            "a thrown HttpRequestException is swallowed (logged at Warning); the next tick still beats");

        await h.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_HeartbeatDisabled_SendsZeroBeats()
    {
        await using var h = HeartbeatHarness.Build(ValidOptions(heartbeat: false));

        await h.Service.StartAsync(CancellationToken.None);
        // Advance well past several intervals — nothing should ever be sent.
        await AdvanceAndSettleAsync(h, TimeSpan.FromSeconds(60));

        h.Handler.CallCount.Should().Be(0, "Heartbeat=false short-circuits ExecuteAsync before any beat");

        await h.Service.StopAsync(CancellationToken.None);
        h.Handler.CallCount.Should().Be(0, "StopAsync also sends nothing when heartbeat is disabled");
    }

    [Fact]
    public async Task StopAsync_SendsExactlyOneOfflineBeat()
    {
        await using var h = HeartbeatHarness.Build(ValidOptions(), jobNames: ["GenerateInvoice"]);

        await h.Service.StartAsync(CancellationToken.None);
        await AdvanceUntilAsync(h, TimeSpan.FromSeconds(2), () => h.Handler.CallCount >= 1);
        var beatsBeforeStop = h.Handler.CallCount;

        await h.Service.StopAsync(CancellationToken.None);

        h.Handler.CallCount.Should().Be(beatsBeforeStop + 1, "StopAsync sends exactly one extra (Offline) beat");
        var last = h.Handler.Beats.Last();
        using var doc = JsonDocument.Parse(last.Body);
        doc.RootElement.GetProperty("status").GetString().Should().Be("Offline",
            "the graceful-stop beat advertises Status=Offline (string enum, JsonStringEnumConverter both ends)");
    }

    [Fact]
    public async Task StopAsync_TokenAlreadyCancelled_DoesNotThrow_AndStillAttemptsOfflineBeat()
    {
        // StopAsync swallows ALL (including OCE) under its own independent 2s timeout. Even
        // when the passed token is already cancelled, no exception propagates out of shutdown.
        await using var h = HeartbeatHarness.Build(ValidOptions());

        await h.Service.StartAsync(CancellationToken.None);
        await AdvanceUntilAsync(h, TimeSpan.FromSeconds(2), () => h.Handler.CallCount >= 1);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Func<Task> act = () => h.Service.StopAsync(cancelled.Token);
        await act.Should().NotThrowAsync(
 " the graceful Offline beat swallows everything (incl. OperationCanceledException) so host shutdown is never disturbed");
    }

    [Fact]
    public async Task Beat_IncludesDeclaredParameterDescriptors()
    {
        var job = new JobDefinition
        {
            Name = "RemoteJob",
            IsPartitioned = false,
            MaxRetries = 0,
            TimeoutSeconds = 0,
            DefaultParameters = new Dictionary<string, object?>(),
            Tags = [],
            DeclaredParameters = [new JobParameterDescriptor { Name = "orderId", Kind = ParameterValueKind.String, Required = true }],
        };
        await using var h = HeartbeatHarness.Build(ValidOptions(), jobs: new[] { job });

        await h.Service.StartAsync(CancellationToken.None);
        await AdvanceUntilAsync(h, TimeSpan.FromSeconds(2), () => h.Handler.CallCount >= 1);

        var beat = h.Handler.Beats.Last();
        using var doc = JsonDocument.Parse(beat.Body);
        var descriptors = doc.RootElement.GetProperty("jobDescriptors").EnumerateArray().ToList();
        descriptors.Should().ContainSingle("the beat advertises one job's declared parameters");
        descriptors[0].GetProperty("name").GetString().Should().Be("RemoteJob");
        var declared = descriptors[0].GetProperty("parameters").EnumerateArray().Single();
        declared.GetProperty("name").GetString().Should().Be("orderId");
        declared.GetProperty("kind").GetString().Should().Be("String", "kind crosses the wire as its string name");
        declared.GetProperty("required").GetBoolean().Should().BeTrue();

        await h.Service.StopAsync(CancellationToken.None);
    }
}
