using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Workers;

namespace UKBatch.Worker;

/// <summary>
/// Background service that POSTs a periodic <see cref="WorkerBeatRequest"/> to the server's
/// <c>/api/workers/beat</c> endpoint so the worker appears (live, TTL'd) in the dashboard Workers panel.
/// </summary>
/// <remarks>
/// <para>
/// Observability ONLY. A down/unreachable server must NEVER crash a healthy worker, so every beat
/// swallows all transport exceptions (logged at Warning) and the loop survives. Dispatch is reached over
/// the configured <c>ITransport</c>, completely independent of this heartbeat.
/// </para>
/// <para>
/// <see cref="WorkerStatus"/> serializes as a string on BOTH ends — this client adds a
/// <see cref="JsonStringEnumConverter"/>, and the server's JSON options do likewise.
/// </para>
/// </remarks>
internal sealed class WorkerHeartbeatService : BackgroundService
{
    /// <summary>Named <c>HttpClient</c> logical name (base address = normalized <c>ServerUrl</c>).</summary>
    public const string HttpClientName = "UKBatch.Worker.Heartbeat";

    /// <summary>Independent timeout for the best-effort graceful-stop Offline beat.</summary>
    private static readonly TimeSpan StopBeatTimeout = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptions<WorkerOptions> _options;
    private readonly IJobDefinitionLookup _jobs;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkerHeartbeatService> _logger;

    public WorkerHeartbeatService(
        IHttpClientFactory httpFactory,
        IOptions<WorkerOptions> options,
        IJobDefinitionLookup jobs,
        TimeProvider timeProvider,
        ILogger<WorkerHeartbeatService> logger)
    {
        ArgumentNullException.ThrowIfNull(httpFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _httpFactory = httpFactory;
        _options = options;
        _jobs = jobs;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.Value;
        if (!opts.Heartbeat)
        {
            _logger.LogInformation("Worker '{Worker}' heartbeat disabled (Heartbeat=false).", opts.WorkerName);
            return;
        }

        // Snapshot job names ONCE — the registration set is immutable after AddUKBatch.
        var jobNames = _jobs.All().Select(j => j.Name).ToArray();

        // Small startup jitter so a fleet of N workers booting together does not thundering-herd the
        // server's /beat endpoint on the exact same tick.
        var jitterMs = Random.Shared.Next(0, 1000);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(jitterMs), _timeProvider, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(opts.HeartbeatInterval, _timeProvider);

        // Fire one immediate beat, then on each tick.
        await BeatOnceAsync(opts, jobNames, WorkerStatus.Online, stoppingToken).ConfigureAwait(false);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await BeatOnceAsync(opts, jobNames, WorkerStatus.Online, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful stop — the host is shutting down.
        }
    }

    /// <summary>
    /// Sends one beat. Swallows ALL transport failures (logged at Warning) so the loop survives a down
    /// server, but RETHROWS an <see cref="OperationCanceledException"/> tied to <paramref name="ct"/> so
    /// the steady-state loop honors host shutdown promptly.
    /// </summary>
    private async Task BeatOnceAsync(WorkerOptions opts, string[] jobNames, WorkerStatus status, CancellationToken ct)
    {
        try
        {
            var client = _httpFactory.CreateClient(HttpClientName);
            var payload = BuildBeat(opts, jobNames, status);
            using var res = await client
                .PostAsJsonAsync("api/workers/beat", payload, JsonOptions, ct)
                .ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Worker '{Worker}' beat returned HTTP {Status} from server.",
                    opts.WorkerName, (int)res.StatusCode);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // honor shutdown — only the steady-state loop calls this path
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Worker '{Worker}' heartbeat to server failed (will retry next tick). Dispatch is UNAFFECTED.",
                opts.WorkerName);
        }
    }

    /// <summary>
    /// Best-effort graceful deregister: on stop, send ONE <see cref="WorkerStatus.Offline"/> beat
    /// so the dashboard reflects the planned shutdown immediately rather than waiting for the TTL. This
    /// path SWALLOWS EVERYTHING (including <see cref="OperationCanceledException"/>) under its OWN
    /// independent 2s timeout — it MUST NOT rethrow out of host shutdown.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        var opts = _options.Value;
        if (opts.Heartbeat && !string.IsNullOrWhiteSpace(opts.ServerUrl))
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(StopBeatTimeout);
            try
            {
                var jobNames = _jobs.All().Select(j => j.Name).ToArray();
                var client = _httpFactory.CreateClient(HttpClientName);
                var payload = BuildBeat(opts, jobNames, WorkerStatus.Offline);
                using var res = await client
                    .PostAsJsonAsync("api/workers/beat", payload, JsonOptions, timeoutCts.Token)
                    .ConfigureAwait(false);
                // Status code irrelevant on shutdown — best-effort only.
            }
            catch (Exception ex)
            {
                // Swallow ALL — a failed/cancelled deregister must not disturb host shutdown.
                _logger.LogDebug(
                    ex,
                    "Worker '{Worker}' graceful Offline beat did not complete (best-effort).",
                    opts.WorkerName);
            }
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private static WorkerBeatRequest BuildBeat(WorkerOptions opts, string[] jobNames, WorkerStatus status) => new()
    {
        Name = opts.WorkerName,
        Jobs = jobNames,
        Tags = opts.Tags ?? Array.Empty<string>(),
        Status = status,
        InFlight = 0, // v0.1: not wired to dispatcher counters (v0.2). Always 0.
        Capacity = 0, // v0.1: reserved (0 = "unknown/unbounded").
    };
}
