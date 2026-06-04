using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Workers;
using UKBatch.Api.Approvals;
using UKBatch.Api.Batches;
using UKBatch.Api.Common;
using UKBatch.Api.Executions;
using UKBatch.Api.Hub;
using UKBatch.Api.Jobs;
using UKBatch.Dashboard.Configuration;
using UKBatch.Runtime;

namespace UKBatch.Dashboard.Clients;

/// <summary>
/// Default <see cref="IUKBatchClient"/> implementation. Combined REST (typed
/// <see cref="HttpClient"/>) + SignalR (<see cref="HubConnection"/>) client per service.
/// </summary>
/// <remarks>
/// <para><b>Lifecycle ownership:</b> the injected <see cref="HttpClient"/> is owned by
/// <see cref="IHttpClientFactory"/> (ASP.NET Core registers it as a singleton). This client does
/// NOT dispose <see cref="HttpClient"/>; only the <see cref="HubConnection"/> + sync primitives
/// are released by <see cref="DisposeAsync"/>.</para>
/// <para><b>Hub event dispatch:</b> subscribers are invoked in parallel via
/// <see cref="Task.WhenAll(System.Collections.Generic.IEnumerable{Task})"/>; one slow
/// subscriber MUST NOT block the others. Per-subscriber exceptions are logged + swallowed.</para>
/// <para><b>EnsureConnected gate:</b> accepts BOTH
/// <see cref="UKBatchClientState.Connected"/> AND
/// <see cref="UKBatchClientState.PartiallyConnected"/>. PartiallyConnected means the hub itself
/// is up; only some PRE-EXISTING re-subscribed groups failed. New subscribe calls still succeed.</para>
/// </remarks>
internal sealed class RestUKBatchClient : IUKBatchClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        // OpenApi convention: enums serialize as STRING (e.g. "Completed", not 2).
        // Mirrors UKBatch.Api.OpenApi.EnumStringTransformer + Sample.RestApi Program.cs config.
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly UKBatchServiceDescriptor _descriptor;
    private readonly HttpClient _http;
    private readonly HubConnection _hub;
    private readonly ILogger<RestUKBatchClient> _logger;
    private readonly DashboardOptions _options;
    private readonly LruDedupeCache<string> _executionDedupe;
    private readonly LruDedupeCache<string> _progressDedupe;
    private readonly LruDedupeCache<string> _batchCompleteDedupe;
    private readonly ConcurrentDictionary<string, byte> _activeGroups = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private int _state; // backing field for State (UKBatchClientState as int for Interlocked)
    private int _connectFailureCount; // for circuit breaker (UKBatchServiceConductor reads this)
    private int _disposed; // 0 = live, 1 = disposed

    public RestUKBatchClient(
        UKBatchServiceDescriptor descriptor,
        HttpClient http,
        ILogger<RestUKBatchClient> logger,
        IOptions<DashboardOptions> options)
        : this(descriptor, http, logger, options, hubConnection: null)
    {
    }

    /// <summary>
    /// Test-only constructor: inject a pre-built <see cref="HubConnection"/> bridged to a
    /// <c>WebApplicationFactory</c> <c>TestServer</c> (LongPolling pattern).
    /// </summary>
    internal RestUKBatchClient(
        UKBatchServiceDescriptor descriptor,
        HttpClient http,
        ILogger<RestUKBatchClient> logger,
        IOptions<DashboardOptions> options,
        HubConnection? hubConnection)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);

        _descriptor = descriptor;
        _http = http;
        _logger = logger;
        _options = options.Value;

        _executionDedupe = new LruDedupeCache<string>(_options.DedupeCacheCapacity);
        _progressDedupe = new LruDedupeCache<string>(_options.DedupeCacheCapacity);
        _batchCompleteDedupe = new LruDedupeCache<string>(_options.DedupeCacheCapacity);

        if (hubConnection is null)
        {
            // Hub URL: BaseUrl is .../api; HubPath is /hubs/jobs → final /api/hubs/jobs
            var hubUrl = new Uri(_descriptor.BaseUrl, _descriptor.HubPath.TrimStart('/'));
            _hub = BuildHubConnection(hubUrl);
        }
        else
        {
            _hub = hubConnection;
        }

        _hub.Reconnecting += OnHubReconnectingAsync;
        _hub.Reconnected += OnHubReconnectedAsync;
        _hub.Closed += OnHubClosedAsync;

        // Wire client→server hub event handlers — dedupe filter THEN invoke local event.
        _hub.On<JobExecution>(nameof(IJobStatusHubClient.ExecutionStateChanged), HandleExecutionStateChangedAsync);
        _hub.On<ProgressBeat>(nameof(IJobStatusHubClient.ProgressUpdated), HandleProgressUpdatedAsync);
        _hub.On<PendingApproval>(nameof(IJobStatusHubClient.ApprovalRequested), HandleApprovalRequestedAsync);
        _hub.On<BatchCompletionSummary>(nameof(IJobStatusHubClient.BatchCompleted), HandleBatchCompletedAsync);
    }

    // ── Identity + lifecycle ───────────────────────────────────────────────────────────

    public UKBatchServiceDescriptor Service => _descriptor;

    public UKBatchClientState State => (UKBatchClientState)Volatile.Read(ref _state);

    public event Func<UKBatchClientState, Task>? StateChanged;

    /// <summary>For tests + UKBatchServiceConductor observational use; reset on successful connect.</summary>
    internal int ConnectFailureCount => Volatile.Read(ref _connectFailureCount);

    /// <summary>Test-only: snapshot of currently-tracked hub groups (for re-subscribe-on-reconnect contract).</summary>
    internal IReadOnlyCollection<string> ActiveGroupsSnapshot => _activeGroups.Keys.ToArray();

    /// <summary>Test-only: force a state transition for unit testing the reconnect path.</summary>
    internal void SetStateForTest(UKBatchClientState next) => TransitionTo(next);

    /// <summary>Test-only: register a synthetic group so the reconnect handler can exercise the re-subscribe path.</summary>
    internal void TrackGroupForTest(string groupKey) => _activeGroups.TryAdd(groupKey, 0);

    /// <summary>Test-only: invoke the reconnect handler directly (bypassing HubConnection wiring).</summary>
    internal Task InvokeReconnectedForTestAsync(string? newConnectionId) => OnHubReconnectedAsync(newConnectionId);

    public async Task ConnectAsync(CancellationToken ct)
    {
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if ((UKBatchClientState)Volatile.Read(ref _state) == UKBatchClientState.Connected)
                return;
            TransitionTo(UKBatchClientState.Connecting);
            try
            {
                await _hub.StartAsync(ct).ConfigureAwait(false);
                TransitionTo(UKBatchClientState.Connected);
                Interlocked.Exchange(ref _connectFailureCount, 0);
                _logger.LogInformation("UKBatch client connected: {Service}", _descriptor.Name);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _connectFailureCount);
                TransitionTo(UKBatchClientState.Disconnected);
                _logger.LogWarning(ex, "UKBatch client connect failed: {Service} (failures={Count})",
                    _descriptor.Name, _connectFailureCount);
                throw;
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        // Once disposed, DisconnectAsync is a silent no-op. Without this
        // guard, awaiting the disposed _connectLock throws ObjectDisposedException — an unfriendly
        // surprise on host shutdown paths that race the disposer.
        if (Volatile.Read(ref _disposed) != 0) return;
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if ((UKBatchClientState)Volatile.Read(ref _state) == UKBatchClientState.Disconnected)
                return;
            await _hub.StopAsync(ct).ConfigureAwait(false);
            TransitionTo(UKBatchClientState.Disconnected);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    // ── REST — Jobs (3) ────────────────────────────────────────────────────────────────

    public async Task<PageEnvelope<JobDefinitionDto>> ListJobsAsync(int offset, int limit, bool? partitioned, CancellationToken ct)
    {
        var qs = $"?offset={offset}&limit={limit}";
        if (partitioned.HasValue) qs += $"&partitioned={partitioned.Value.ToString().ToLowerInvariant()}";
        using var req = new HttpRequestMessage(HttpMethod.Get, $"jobs{qs}");
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        return await DeserializeOrThrowAsync<PageEnvelope<JobDefinitionDto>>(res, ct).ConfigureAwait(false);
    }

    public async Task<JobDefinitionDto?> GetJobAsync(string jobName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobName);
        using var res = await _http.GetAsync($"jobs/{Uri.EscapeDataString(jobName)}", ct).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        return await DeserializeOrThrowAsync<JobDefinitionDto>(res, ct).ConfigureAwait(false);
    }

    public async Task<string> TriggerJobAsync(string jobName, IReadOnlyDictionary<string, object?>? parameters, string? triggeredBy, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobName);
        var body = new JobTriggerRequest { Parameters = parameters, TriggeredBy = triggeredBy };
        using var res = await _http.PostAsJsonAsync($"jobs/{Uri.EscapeDataString(jobName)}/trigger", body, JsonOptions, ct).ConfigureAwait(false);
        var payload = await DeserializeOrThrowAsync<JobTriggerResponse>(res, ct).ConfigureAwait(false);
        return payload.ExecutionId;
    }

    // ── REST — Batches (5) ─────────────────────────────────────────────────────────────

    public async Task<PageEnvelope<BatchDefinitionDto>> ListBatchesAsync(int offset, int limit, string? nameContains, BatchSource? source, CancellationToken ct)
    {
        var qs = $"?offset={offset}&limit={limit}";
        if (!string.IsNullOrEmpty(nameContains)) qs += $"&nameContains={Uri.EscapeDataString(nameContains)}";
        if (source.HasValue) qs += $"&source={source.Value}";
        using var req = new HttpRequestMessage(HttpMethod.Get, $"batches{qs}");
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        return await DeserializeOrThrowAsync<PageEnvelope<BatchDefinitionDto>>(res, ct).ConfigureAwait(false);
    }

    public async Task<BatchDefinitionDto?> GetBatchByIdAsync(string definitionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(definitionId);
        using var res = await _http.GetAsync($"batches/by-id/{Uri.EscapeDataString(definitionId)}", ct).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        return await DeserializeOrThrowAsync<BatchDefinitionDto>(res, ct).ConfigureAwait(false);
    }

    public async Task<BatchDefinitionDto?> GetBatchByNameAsync(string name, BatchSource? source, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var path = $"batches/by-name/{Uri.EscapeDataString(name)}";
        if (source.HasValue) path += $"?source={source.Value}";
        using var res = await _http.GetAsync(path, ct).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        return await DeserializeOrThrowAsync<BatchDefinitionDto>(res, ct).ConfigureAwait(false);
    }

    public async Task<string> RunBatchByIdAsync(string definitionId, IReadOnlyDictionary<string, object?>? initialParameters, string? triggeredBy, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(definitionId);
        var body = new BatchRunRequest { InitialParameters = initialParameters, TriggeredBy = triggeredBy };
        using var res = await _http.PostAsJsonAsync($"batches/by-id/{Uri.EscapeDataString(definitionId)}/run", body, JsonOptions, ct).ConfigureAwait(false);
        var payload = await DeserializeOrThrowAsync<BatchRunResponse>(res, ct).ConfigureAwait(false);
        return payload.BatchId;
    }

    public async Task<PageEnvelope<JobExecution>> GetBatchRunStatusAsync(string batchRunId, int offset, int limit, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchRunId);
        var qs = $"?offset={offset}&limit={limit}";
        using var res = await _http.GetAsync($"batches/{Uri.EscapeDataString(batchRunId)}/status{qs}", ct).ConfigureAwait(false);
        return await DeserializeOrThrowAsync<PageEnvelope<JobExecution>>(res, ct).ConfigureAwait(false);
    }

    public async Task<BatchDefinitionDto> CreateBatchAsync(CreateBatchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var res = await _http.PostAsJsonAsync("batches", request, JsonOptions, ct).ConfigureAwait(false);
        return await DeserializeOrThrowAsync<BatchDefinitionDto>(res, ct).ConfigureAwait(false);
    }

    public async Task<BatchDefinitionDto> UpdateBatchAsync(string definitionId, UpdateBatchRequest request, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(definitionId);
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(definitionId, request.Id, StringComparison.Ordinal))
            throw new ArgumentException("definitionId must match request.Id.", nameof(definitionId));
        using var res = await _http.PutAsJsonAsync(
            $"batches/by-id/{Uri.EscapeDataString(definitionId)}", request, JsonOptions, ct).ConfigureAwait(false);
        return await DeserializeOrThrowAsync<BatchDefinitionDto>(res, ct).ConfigureAwait(false);
    }

    public async Task DeleteBatchAsync(string definitionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(definitionId);
        using var res = await _http.DeleteAsync(
            $"batches/by-id/{Uri.EscapeDataString(definitionId)}", ct).ConfigureAwait(false);
        // DELETE is idempotent server-side (NoContent even when absent). A non-2xx here would be a
        // ProblemDetails (e.g. code-source 400) — let ThrowIfErrorAsync surface it. 204 → no throw.
        await ThrowIfErrorAsync(res, ct).ConfigureAwait(false);
    }

    // ── REST — Executions (3) ──────────────────────────────────────────────────────────

    public async Task<JobExecution?> GetExecutionAsync(string executionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(executionId);
        using var res = await _http.GetAsync($"executions/{Uri.EscapeDataString(executionId)}", ct).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        return await DeserializeOrThrowAsync<JobExecution>(res, ct).ConfigureAwait(false);
    }

    public async Task<PageEnvelope<JobExecution>> QueryExecutionsAsync(JobQueryRequest query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        using var res = await _http.PostAsJsonAsync("executions/query", query, JsonOptions, ct).ConfigureAwait(false);
        return await DeserializeOrThrowAsync<PageEnvelope<JobExecution>>(res, ct).ConfigureAwait(false);
    }

    public async Task CancelExecutionAsync(string executionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(executionId);
        using var content = new StringContent(string.Empty);
        using var res = await _http.PostAsync($"executions/{Uri.EscapeDataString(executionId)}/cancel", content, ct).ConfigureAwait(false);
        await ThrowIfErrorAsync(res, ct).ConfigureAwait(false);
    }

    // ── REST — Approvals (3) ───────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<PendingApprovalDto>> ListApprovalsAsync(string? role, CancellationToken ct)
    {
        // Strip the artificial PageEnvelope returned by /approvals.
        var path = "approvals";
        if (!string.IsNullOrEmpty(role)) path += $"?role={Uri.EscapeDataString(role)}";
        using var res = await _http.GetAsync(path, ct).ConfigureAwait(false);
        var envelope = await DeserializeOrThrowAsync<PageEnvelope<PendingApprovalDto>>(res, ct).ConfigureAwait(false);
        return envelope.Items;
    }

    public async Task ApproveAsync(string approvalId, string? note, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(approvalId);
        var body = new ApprovalNoteRequest { Note = note };
        using var res = await _http.PostAsJsonAsync($"approvals/{Uri.EscapeDataString(approvalId)}/approve", body, JsonOptions, ct).ConfigureAwait(false);
        await ThrowIfErrorAsync(res, ct).ConfigureAwait(false);
    }

    public async Task RejectAsync(string approvalId, string reason, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(approvalId);
        ArgumentException.ThrowIfNullOrEmpty(reason);
        var body = new ApprovalReasonRequest { Reason = reason };
        using var res = await _http.PostAsJsonAsync($"approvals/{Uri.EscapeDataString(approvalId)}/reject", body, JsonOptions, ct).ConfigureAwait(false);
        await ThrowIfErrorAsync(res, ct).ConfigureAwait(false);
    }

    // ── REST — Workers (1) ─────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<WorkerInfo>> GetWorkersAsync(CancellationToken ct)
    {
        // Pure REST — NO EnsureConnected (that gate is hub-only, mirroring ListJobsAsync /
        // ListApprovalsAsync). The Workers panel polls this on a timer; a down hub must not
        // block the snapshot. WorkerStatus crosses the wire as a string (JsonStringEnumConverter).
        using var res = await _http.GetAsync("workers", ct).ConfigureAwait(false);
        return await DeserializeOrThrowAsync<List<WorkerInfo>>(res, ct).ConfigureAwait(false);
    }

    // ── Hub events (4) ─────────────────────────────────────────────────────────────────

    public event Func<JobExecution, Task>? ExecutionStateChanged;
    public event Func<ProgressBeat, Task>? ProgressUpdated;
    public event Func<PendingApproval, Task>? ApprovalRequested;
    public event Func<BatchCompletionSummary, Task>? BatchCompleted;

    // ── Hub subscriptions (8) ──────────────────────────────────────────────────────────

    public async Task SubscribeToExecutionAsync(string executionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(executionId);
        EnsureConnected();
        // S-1: track BEFORE invoke so a mid-flight reconnect picks up the group from
        // _activeGroups. Roll back the entry if the server-side invoke fails so we don't
        // keep a phantom subscription on this client. Idempotent: if the caller already
        // owns this group, short-circuit without a duplicate invoke.
        var group = $"exec:{executionId}";
        if (!_activeGroups.TryAdd(group, 0)) return;
        try
        {
            await _hub.InvokeAsync("SubscribeToExecution", executionId, ct).ConfigureAwait(false);
        }
        catch
        {
            _activeGroups.TryRemove(group, out _);
            throw;
        }
    }

    public async Task UnsubscribeFromExecutionAsync(string executionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(executionId);
        if ((UKBatchClientState)Volatile.Read(ref _state) is not (UKBatchClientState.Connected or UKBatchClientState.PartiallyConnected)) return;
        await _hub.InvokeAsync("UnsubscribeExecution", executionId, ct).ConfigureAwait(false);
        _activeGroups.TryRemove($"exec:{executionId}", out _);
    }

    public async Task SubscribeToBatchAsync(string batchRunId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchRunId);
        EnsureConnected();
        // S-1: track-before-invoke + rollback on failure (see SubscribeToExecutionAsync).
        var group = $"batch:{batchRunId}";
        if (!_activeGroups.TryAdd(group, 0)) return;
        try
        {
            await _hub.InvokeAsync("SubscribeToBatch", batchRunId, ct).ConfigureAwait(false);
        }
        catch
        {
            _activeGroups.TryRemove(group, out _);
            throw;
        }
    }

    public async Task UnsubscribeFromBatchAsync(string batchRunId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchRunId);
        if ((UKBatchClientState)Volatile.Read(ref _state) is not (UKBatchClientState.Connected or UKBatchClientState.PartiallyConnected)) return;
        await _hub.InvokeAsync("UnsubscribeBatch", batchRunId, ct).ConfigureAwait(false);
        _activeGroups.TryRemove($"batch:{batchRunId}", out _);
    }

    public async Task SubscribeToJobAsync(string jobName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobName);
        EnsureConnected();
        // S-1: track-before-invoke + rollback on failure (see SubscribeToExecutionAsync).
        var group = $"job:{jobName}";
        if (!_activeGroups.TryAdd(group, 0)) return;
        try
        {
            await _hub.InvokeAsync("SubscribeToJob", jobName, ct).ConfigureAwait(false);
        }
        catch
        {
            _activeGroups.TryRemove(group, out _);
            throw;
        }
    }

    public async Task UnsubscribeFromJobAsync(string jobName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobName);
        if ((UKBatchClientState)Volatile.Read(ref _state) is not (UKBatchClientState.Connected or UKBatchClientState.PartiallyConnected)) return;
        await _hub.InvokeAsync("UnsubscribeJob", jobName, ct).ConfigureAwait(false);
        _activeGroups.TryRemove($"job:{jobName}", out _);
    }

    public async Task SubscribeAllAsync(CancellationToken ct)
    {
        EnsureConnected();
        // S-1: track-before-invoke + rollback on failure (see SubscribeToExecutionAsync).
        const string group = "all";
        if (!_activeGroups.TryAdd(group, 0)) return;
        try
        {
            await _hub.InvokeAsync("SubscribeAll", ct).ConfigureAwait(false);
        }
        catch
        {
            _activeGroups.TryRemove(group, out _);
            throw;
        }
    }

    public async Task UnsubscribeAllAsync(CancellationToken ct)
    {
        if ((UKBatchClientState)Volatile.Read(ref _state) is not (UKBatchClientState.Connected or UKBatchClientState.PartiallyConnected)) return;
        await _hub.InvokeAsync("UnsubscribeAll", ct).ConfigureAwait(false);
        _activeGroups.TryRemove("all", out _);
    }

    // ── Internal seams (hub plumbing, dedupe, reconnect) ───────────────────────────────

    /// <summary>
    /// Builds the SignalR <see cref="HubConnection"/> for this client. Configures
    /// <c>WithUrl</c> (incl. optional <c>X-Api-Key</c> header), <c>WithAutomaticReconnect</c>
    /// (jittered delays per <see cref="BuildReconnectDelays"/>), and the message handlers.
    /// </summary>
    private HubConnection BuildHubConnection(Uri hubUrl)
    {
        return new HubConnectionBuilder()
            .WithUrl(hubUrl.ToString(), hubOpts =>
            {
                if (!string.IsNullOrEmpty(_descriptor.ApiKey))
                {
                    hubOpts.Headers["X-Api-Key"] = _descriptor.ApiKey;
                }
                // General static-header seam (symmetry with the REST configurator) — bearer / API-key /
                // dev-auth headers forwarded on the SignalR negotiate + connection.
                if (_descriptor.Headers is { Count: > 0 } headers)
                {
                    foreach (var (k, v) in headers)
                    {
                        hubOpts.Headers[k] = v;
                    }
                }
            })
            .WithAutomaticReconnect(BuildReconnectDelays())
            .Build();
    }

    /// <summary>
    /// Reconnect delays — jitter contract. Custom values come from
    /// <see cref="DashboardOptions.ReconnectDelays"/>; otherwise defaults to
    /// <c>[2s+rand(0,1s), 5s+rand(0,2s), 10s+rand(0,3s), 30s+rand(0,5s)]</c>.
    /// </summary>
    private TimeSpan[] BuildReconnectDelays()
    {
        if (_options.ReconnectDelays is { Count: > 0 } configured)
        {
            return configured.ToArray();
        }
        var rng = Random.Shared;
        return new[]
        {
            TimeSpan.FromMilliseconds(2000 + rng.Next(0, 1000)),
            TimeSpan.FromMilliseconds(5000 + rng.Next(0, 2000)),
            TimeSpan.FromMilliseconds(10000 + rng.Next(0, 3000)),
            TimeSpan.FromMilliseconds(30000 + rng.Next(0, 5000)),
        };
    }

    /// <summary>
    /// <see cref="Interlocked.Exchange(ref int, int)"/> snapshot + fire-and-forget MUST
    /// happen OUTSIDE any caller-held lock. <see cref="ConnectAsync"/> / <see cref="DisconnectAsync"/>
    /// hold <c>_connectLock</c> when calling <see cref="TransitionTo"/>; a subscriber that re-enters
    /// <see cref="ConnectAsync"/> would deadlock if we awaited the dispatch under the same lock.
    /// The fire-and-forget pattern below is correct ONLY because the actual await happens on a
    /// thread-pool continuation, which cannot be running inside <c>_connectLock</c>.
    /// </summary>
    private void TransitionTo(UKBatchClientState next)
    {
        var prev = (UKBatchClientState)Interlocked.Exchange(ref _state, (int)next);
        if (prev == next) return;
        _ = InvokeStateChangedAsync(next); // fire-and-forget on the thread pool; NOT awaited under any lock
    }

    private async Task InvokeStateChangedAsync(UKBatchClientState next)
    {
        var handler = StateChanged;
        if (handler is null) return;
        foreach (Func<UKBatchClientState, Task> sub in handler.GetInvocationList())
        {
            try
            {
                await sub(next).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StateChanged subscriber threw — isolated.");
            }
        }
    }

    /// <summary>
    /// Guards subscribe entry points. Accepts BOTH <see cref="UKBatchClientState.Connected"/>
    /// and <see cref="UKBatchClientState.PartiallyConnected"/>. PartiallyConnected means the hub
    /// connection IS up; only some PRE-EXISTING group subscriptions failed to re-establish on the
    /// last reconnect. New subscribes (this call's most likely intent) STILL reach the server.
    /// </summary>
    /// <remarks>
    /// <para>Rejecting PartiallyConnected here would be a regression — page navigation to
    /// a brand-new execution detail would crash mid-degraded-state even though the hub itself
    /// was healthy for new groups.</para>
    /// <para>Also gates on <see cref="_disposed"/>. After <see cref="DisposeAsync"/>
    /// runs, the hub connection is gone, the connect-lock is disposed, and any subscribe attempt would
    /// race against teardown. Throw <see cref="ObjectDisposedException"/> so callers get the right
    /// signal (NOT a misleading <see cref="InvalidOperationException"/>).</para>
    /// </remarks>
    private void EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var state = (UKBatchClientState)Volatile.Read(ref _state);
        if (state != UKBatchClientState.Connected && state != UKBatchClientState.PartiallyConnected)
        {
            throw new InvalidOperationException(
                $"UKBatch client '{_descriptor.Name}' is in state {state}; subscribe operations require Connected or PartiallyConnected. " +
                "If PartiallyConnected, the hub connection is up but some pre-existing group subscriptions failed to re-establish; new subscriptions still work, but consider clicking Retry on the ConnectionBanner to clear the degraded state.");
        }
    }

    // ── Hub event handlers — dedupe + parallel-isolated subscriber dispatch ────────────

    private async Task HandleExecutionStateChangedAsync(JobExecution exec)
    {
        var key = $"{exec.ExecutionId}|{exec.Status}|{exec.AttemptNumber}";
        if (!_executionDedupe.TryAdd(key))
        {
            // Dedupe HIT — already delivered this exact (id, status, attempt). Drop.
            return;
        }
        var handler = ExecutionStateChanged;
        if (handler is null) return;
        await DispatchAsync(handler, exec, nameof(ExecutionStateChanged)).ConfigureAwait(false);
    }

    private async Task HandleProgressUpdatedAsync(ProgressBeat beat)
    {
        var key = $"{beat.ExecutionId}|{beat.Processed}|{beat.Failed}";
        if (!_progressDedupe.TryAdd(key)) return;
        var handler = ProgressUpdated;
        if (handler is null) return;
        await DispatchAsync(handler, beat, nameof(ProgressUpdated)).ConfigureAwait(false);
    }

    private async Task HandleApprovalRequestedAsync(PendingApproval p)
    {
        // No dedupe — approval events are rare (one per gate, lifecycle ~minutes).
        var handler = ApprovalRequested;
        if (handler is null) return;
        await DispatchAsync(handler, p, nameof(ApprovalRequested)).ConfigureAwait(false);
    }

    private async Task HandleBatchCompletedAsync(BatchCompletionSummary summary)
    {
        if (!_batchCompleteDedupe.TryAdd(summary.BatchId)) return;
        var handler = BatchCompleted;
        if (handler is null) return;
        await DispatchAsync(handler, summary, nameof(BatchCompleted)).ConfigureAwait(false);
    }

    /// <summary>Parallel-isolated dispatch — one slow/throwing subscriber MUST NOT block others.</summary>
    private async Task DispatchAsync<T>(Func<T, Task> handler, T payload, string eventName)
    {
        var tasks = handler.GetInvocationList()
            .Cast<Func<T, Task>>()
            .Select(sub => InvokeAndSwallowAsync(sub, payload, eventName));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task InvokeAndSwallowAsync<T>(Func<T, Task> sub, T payload, string eventName)
    {
        try
        {
            await sub(payload).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Event} subscriber threw — isolated.", eventName);
        }
    }

    // ── Reconnect handlers ─────────────────────────────────────────────────────────────

    private Task OnHubReconnectingAsync(Exception? ex)
    {
        TransitionTo(UKBatchClientState.Reconnecting);
        if (ex is not null)
            _logger.LogWarning(ex, "UKBatch client reconnecting: {Service}", _descriptor.Name);
        return Task.CompletedTask;
    }

    private async Task OnHubReconnectedAsync(string? newConnectionId)
    {
        // The hub LOSES group memberships on reconnect. We must re-subscribe
        // to every group we held before the disconnect.
        _logger.LogInformation("UKBatch client reconnected: {Service} (connectionId={ConnId}, groups={Count})",
            _descriptor.Name, newConnectionId, _activeGroups.Count);

        // Track per-group resubscribe failures. If ANY group fails, transition to
        // PartiallyConnected (NOT Connected) so operators see the degraded state in the UI. Without
        // this guard, a partial-resubscribe storm would silently report "Connected = green" while
        // events for failed groups never arrive — silent failure mode.
        var groups = _activeGroups.Keys.ToArray();
        var failedGroups = new List<string>();
        foreach (var group in groups)
        {
            try
            {
                await ReinvokeGroupAsync(group).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Re-subscribe to group {Group} failed; will retry on next reconnect cycle.", group);
                failedGroups.Add(group);
                // Don't remove from _activeGroups — next reconnect cycle will retry.
            }
        }

        var nextState = failedGroups.Count > 0
            ? UKBatchClientState.PartiallyConnected
            : UKBatchClientState.Connected;
        TransitionTo(nextState);

        if (failedGroups.Count > 0)
        {
            _logger.LogWarning(
                "Reconnect to {Service}: {Failed} of {Total} group subscriptions failed; operator retry required. Failed groups: {Groups}",
                _descriptor.Name, failedGroups.Count, groups.Length, string.Join(", ", failedGroups));
        }
    }

    private Task ReinvokeGroupAsync(string groupKey)
    {
        // groupKey is one of "exec:<id>", "batch:<id>", "job:<name>", "all"
        var colonIdx = groupKey.IndexOf(':');
        if (colonIdx < 0)
        {
            // "all"
            return _hub.InvokeAsync("SubscribeAll");
        }
        var prefix = groupKey[..colonIdx];
        var id = groupKey[(colonIdx + 1)..];
        return prefix switch
        {
            "exec" => _hub.InvokeAsync("SubscribeToExecution", id),
            "batch" => _hub.InvokeAsync("SubscribeToBatch", id),
            "job" => _hub.InvokeAsync("SubscribeToJob", id),
            _ => Task.CompletedTask,
        };
    }

    private Task OnHubClosedAsync(Exception? ex)
    {
        TransitionTo(UKBatchClientState.Disconnected);
        if (ex is not null)
            _logger.LogWarning(ex, "UKBatch client closed unexpectedly: {Service}", _descriptor.Name);
        return Task.CompletedTask;
    }

    // ── Deserialization + ProblemDetails error mapping ─────────────────────────────────

    /// <summary>
    /// Deserializes a success response into <typeparamref name="T"/>, or throws
    /// <see cref="UKBatchClientException"/> with structured ProblemDetails extraction on error.
    /// </summary>
    /// <remarks>
    /// <c>HttpContentJsonExtensions.ReadFromJsonAsync</c> may throw
    /// <see cref="JsonException"/> if the server claims <c>application/problem+json</c> but emits
    /// malformed JSON. Wrap the parse attempt; on <see cref="JsonException"/> fall through to the
    /// raw-body path so we still surface a structured exception with body excerpt.
    /// </remarks>
    private static async Task<T> DeserializeOrThrowAsync<T>(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode)
        {
            var content = await res.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
            if (content is null)
                throw new UKBatchClientException(
                    $"Server returned empty body for {res.RequestMessage?.RequestUri}.",
                    res.StatusCode);
            return content;
        }
        // Try to parse ProblemDetails.
        if (string.Equals(res.Content.Headers.ContentType?.MediaType, "application/problem+json", StringComparison.OrdinalIgnoreCase))
        {
            ProblemDetails? pd = null;
            try
            {
                pd = await res.Content.ReadFromJsonAsync<ProblemDetails>(JsonOptions, ct).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                // Malformed problem+json — fall through to raw-body path below.
            }
            if (pd is not null)
            {
                IReadOnlyDictionary<string, string[]>? validation = null;
                if (pd.Extensions.TryGetValue("errors", out var errorsRaw) && errorsRaw is JsonElement je && je.ValueKind == JsonValueKind.Object)
                {
                    validation = je.EnumerateObject().ToDictionary(
                        p => p.Name,
                        p => p.Value.EnumerateArray().Select(v => v.GetString() ?? string.Empty).ToArray());
                }
                throw new UKBatchClientException(
                    pd.Title ?? "UKBatch service returned an error",
                    res.StatusCode,
                    pd.Type,
                    pd.Detail,
                    validation);
            }
        }
        var rawBody = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new UKBatchClientException(
            $"UKBatch service returned {(int)res.StatusCode} (no ProblemDetails). Body: {rawBody}",
            res.StatusCode);
    }

    /// <summary>For void-returning REST methods (e.g. cancel, approve, reject) — throws on non-2xx, no body parse on success.</summary>
    private static async Task ThrowIfErrorAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        // Reuse the error-path logic by attempting a deserialize that ignores the success branch.
        if (string.Equals(res.Content.Headers.ContentType?.MediaType, "application/problem+json", StringComparison.OrdinalIgnoreCase))
        {
            ProblemDetails? pd = null;
            try
            {
                pd = await res.Content.ReadFromJsonAsync<ProblemDetails>(JsonOptions, ct).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                // Malformed problem+json — fall through to raw-body path below.
            }
            if (pd is not null)
            {
                IReadOnlyDictionary<string, string[]>? validation = null;
                if (pd.Extensions.TryGetValue("errors", out var errorsRaw) && errorsRaw is JsonElement je && je.ValueKind == JsonValueKind.Object)
                {
                    validation = je.EnumerateObject().ToDictionary(
                        p => p.Name,
                        p => p.Value.EnumerateArray().Select(v => v.GetString() ?? string.Empty).ToArray());
                }
                throw new UKBatchClientException(
                    pd.Title ?? "UKBatch service returned an error",
                    res.StatusCode,
                    pd.Type,
                    pd.Detail,
                    validation);
            }
        }
        var rawBody = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new UKBatchClientException(
            $"UKBatch service returned {(int)res.StatusCode} (no ProblemDetails). Body: {rawBody}",
            res.StatusCode);
    }

    // ── Disposal ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Disposes hub connection + sync primitive. The injected <see cref="HttpClient"/> is NOT
    /// disposed — it is owned by <see cref="IHttpClientFactory"/> (registered as singleton by
    /// ASP.NET Core). Explicit lifecycle contract.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            await _hub.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hub dispose threw — ignoring on shutdown path.");
        }
        _connectLock.Dispose();
        // _http is owned by IHttpClientFactory; do NOT dispose.
        // _executionDedupe, _progressDedupe, _batchCompleteDedupe: no resources beyond memory.
    }
}
