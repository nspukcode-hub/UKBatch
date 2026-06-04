using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UKBatch.Dashboard.Clients;
using UKBatch.Dashboard.Configuration;
using UKBatch.Dashboard.Tests.Common;
using Xunit;

namespace UKBatch.Dashboard.Tests.Clients;

/// <summary>
/// reconnect + EnsureConnected regression tests.
/// Covers (PartiallyConnected) + (subscribe-when-PartiallyConnected).
/// </summary>
public sealed class RestUKBatchClientReconnectTests : IClassFixture<SampleRestApiFactory>
{
    private readonly SampleRestApiFactory _factory;

    public RestUKBatchClientReconnectTests(SampleRestApiFactory factory)
    {
        _factory = factory;
    }

    private RestUKBatchClient BuildWithBridgedHub()
    {
        var http = _factory.CreateClient();
        http.BaseAddress = new Uri(_factory.Server.BaseAddress, "/api/");
        var descriptor = new UKBatchServiceDescriptor
        {
            Name = "self",
            BaseUrl = http.BaseAddress,
        };
        var opts = Options.Create(new DashboardOptions
        {
            DedupeCacheCapacity = 32,
            ReconnectDelays = [TimeSpan.FromMilliseconds(50)],
        });
        var hub = RestUKBatchClientFactory.BuildHubConnection(_factory);
        return new RestUKBatchClient(descriptor, http, NullLogger<RestUKBatchClient>.Instance, opts, hub);
    }

    [Fact]
    public async Task Subscribe_WhenPartiallyConnected_Succeeds()
    {
        // (v1.2) regression lock: subscribing while in PartiallyConnected state must
        // STILL succeed. Rejecting PartiallyConnected here was a regression — page navigation
        // to a brand-new execution detail would have crashed mid-degraded-state even though the
        // hub itself was healthy for new groups.
        await using var client = BuildWithBridgedHub();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client.ConnectAsync(cts.Token);

        // Force PartiallyConnected for the test.
        client.SetStateForTest(UKBatchClientState.PartiallyConnected);
        client.State.Should().Be(UKBatchClientState.PartiallyConnected);

        // Subscribe MUST NOT throw.
        Func<Task> act = () => client.SubscribeToExecutionAsync("exec-new-id", cts.Token);
        await act.Should().NotThrowAsync("NEW-SF-D contract: PartiallyConnected accepts new subscribes.");
        client.ActiveGroupsSnapshot.Should().Contain("exec:exec-new-id");
    }

    [Fact]
    public async Task Reconnect_AllGroupsResubscribeSucceed_TransitionsToConnected()
    {
        await using var client = BuildWithBridgedHub();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client.ConnectAsync(cts.Token);

        // Track some active groups that exist server-side. SubscribeAll is the safest — no string
        // id needed and the hub method is always defined.
        await client.SubscribeAllAsync(cts.Token);
        client.ActiveGroupsSnapshot.Should().Contain("all");

        // Simulate a SignalR reconnect by invoking the handler directly. All re-subscribes
        // should succeed because the hub is healthy.
        await client.InvokeReconnectedForTestAsync("new-connection-id");
        // Give StateChanged fire-and-forget a moment.
        await Task.Delay(150);
        client.State.Should().Be(UKBatchClientState.Connected, "all re-subscribes succeeded → state stays Connected");
    }

    [Fact]
    public async Task Subscribe_TrackedBeforeInvoke_ReconnectRediscoversGroup()
    {
        // (cleanup) regression: Subscribe* methods track the group in _activeGroups
        // BEFORE invoking the server-side subscribe RPC, so a mid-flight reconnect handler can see
        // the entry. Without this, the prior behavior (invoke → TryAdd) opened a window where the
        // hub auto-reconnect handler ran between the invoke and the TryAdd, leaving the group
        // dark on the new connection.
        //
        // The test exercises a normal subscribe, then invokes the reconnect handler directly,
        // and asserts the group is still tracked + the client reaches Connected (the re-subscribe
        // path found the group via _activeGroups).
        await using var client = BuildWithBridgedHub();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client.ConnectAsync(cts.Token);

        await client.SubscribeAllAsync(cts.Token);
        client.ActiveGroupsSnapshot.Should().Contain("all",
 "group MUST be tracked in _activeGroups BEFORE the server-side InvokeAsync");

        // Simulate a reconnect — the handler reads _activeGroups and re-subscribes.
        await client.InvokeReconnectedForTestAsync("new-connection-id");
        await Task.Delay(150);
        client.State.Should().Be(UKBatchClientState.Connected,
            "the tracked group was successfully rediscovered + re-subscribed on reconnect");
        client.ActiveGroupsSnapshot.Should().Contain("all");
    }

    [Fact]
    public async Task Subscribe_InvokeFails_RollsBackTrackedGroup()
    {
        // (cleanup) rollback: when the server-side InvokeAsync throws, the tracked
        // entry MUST be rolled back so this client does not keep a phantom subscription that the
        // server never accepted.
        //
        // We trigger a server-side failure by calling SubscribeToExecution with an empty id — the
        // server-side hub method validates and throws (HubException over the wire). NOTE: the
        // CLIENT-side ArgumentException.ThrowIfNullOrEmpty triggers BEFORE InvokeAsync — pick an
        // id the client accepts but the server rejects. Empty whitespace passes client guard but
        // would fail server-side; safer route: a value the client accepts plus a state where the
        // hub is not running (Disconnected) — that raises a different exception path but still
        // proves rollback.
        //
        // To keep the test deterministic without server cooperation we use a slight hack: invoke
        // the hub manually from a Disconnected client. The Subscribe* methods bypass the
        // EnsureConnected check by transitioning to PartiallyConnected first; from there the
        // hub.InvokeAsync raises an InvalidOperationException ("not connected") which the catch
        // block must roll back.
        await using var client = BuildWithBridgedHub();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client.ConnectAsync(cts.Token);
        await client.DisconnectAsync(cts.Token);

        // Now the hub is stopped but the client object is alive. Force PartiallyConnected to slip
        // past EnsureConnected; the actual InvokeAsync will fail and the rollback should kick in.
        client.SetStateForTest(UKBatchClientState.PartiallyConnected);

        Func<Task> act = () => client.SubscribeToExecutionAsync("rollback-target", cts.Token);
        await act.Should().ThrowAsync<Exception>("hub is stopped — InvokeAsync must throw");

        client.ActiveGroupsSnapshot.Should().NotContain("exec:rollback-target",
 "failed InvokeAsync MUST roll back the tracked group entry");
    }

    [Fact]
    public async Task Reconnect_GroupResubscribeFails_TransitionsToPartiallyConnected()
    {
        // (v1.1) regression lock: when re-subscribe fails for any group, the client
        // MUST transition to PartiallyConnected (NOT Connected) so the UI surfaces a degraded
        // banner. Without this guard, silent failure mode.
        await using var client = BuildWithBridgedHub();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client.ConnectAsync(cts.Token);

        // Track an invalid group whose re-subscribe will throw (empty execution id - hub will
        // ArgumentException on the server). The group format "exec:" with no id triggers
        // ArgumentException.ThrowIfNullOrEmpty server-side.
        client.TrackGroupForTest("exec:");

        // Invoke reconnect handler — the bad group's re-subscribe should fail.
        await client.InvokeReconnectedForTestAsync("new-connection-id");
        await Task.Delay(200);

        client.State.Should().Be(UKBatchClientState.PartiallyConnected,
 "failed re-subscribe must surface as PartiallyConnected.");
    }

    [Fact]
    public async Task Subscribe_AfterDispose_ThrowsObjectDisposed()
    {
        // (cleanup): EnsureConnected gates on _disposed via ObjectDisposedException.
        // After DisposeAsync, any subscribe operation MUST throw ObjectDisposedException — NOT a
        // misleading InvalidOperationException or a NullReferenceException on the disposed hub.
        var client = BuildWithBridgedHub();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client.ConnectAsync(cts.Token);
        await client.DisposeAsync();

        Func<Task> act = () => client.SubscribeToExecutionAsync("any-id", CancellationToken.None);
        await act.Should().ThrowAsync<ObjectDisposedException>(
 "subscribe after dispose MUST throw ObjectDisposedException");
    }

    [Fact]
    public async Task DisconnectAsync_AfterDispose_IsNoOp()
    {
        // (cleanup): DisconnectAsync early-returns on _disposed so the disposed
        // _connectLock is never awaited. The previous behavior crashed with
        // ObjectDisposedException("SemaphoreSlim") on shutdown paths that raced the disposer.
        var client = BuildWithBridgedHub();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client.ConnectAsync(cts.Token);
        await client.DisposeAsync();

        Func<Task> act = () => client.DisconnectAsync(CancellationToken.None);
        await act.Should().NotThrowAsync(
 "DisconnectAsync after dispose MUST be a silent no-op");
    }
}
