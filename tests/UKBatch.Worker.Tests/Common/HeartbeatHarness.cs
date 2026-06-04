using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace UKBatch.Worker.Tests.Common;

/// <summary>
/// Builds a <see cref="WorkerHeartbeatService"/> wired to a <see cref="RecordingHttpMessageHandler"/>
/// (as the named heartbeat client's PRIMARY handler) and a <see cref="FakeTimeProvider"/>, so the
/// loop's cadence is deterministic and every POST is captured. Mirrors the named-client +
/// <c>ConfigurePrimaryHttpMessageHandler</c> pattern from <c>UKBatch.Transport.Http.Tests</c> and the
/// base-address normalization from <c>WorkerModeBuilderExtensions</c>.
/// </summary>
internal sealed class HeartbeatHarness : IAsyncDisposable
{
    public WorkerHeartbeatService Service { get; }
    public RecordingHttpMessageHandler Handler { get; }
    public FakeTimeProvider Time { get; }
    private readonly ServiceProvider _sp;

    private HeartbeatHarness(WorkerHeartbeatService service, RecordingHttpMessageHandler handler, FakeTimeProvider time, ServiceProvider sp)
    {
        Service = service;
        Handler = handler;
        Time = time;
        _sp = sp;
    }

    public static HeartbeatHarness Build(
        WorkerOptions options,
        RecordingHttpMessageHandler? handler = null,
        string[]? jobNames = null)
    {
        handler ??= new RecordingHttpMessageHandler();
        var time = new FakeTimeProvider();

        var services = new ServiceCollection();
        services
            .AddHttpClient(WorkerHeartbeatService.HttpClientName)
            .ConfigureHttpClient(http =>
            {
                if (!string.IsNullOrWhiteSpace(options.ServerUrl))
                {
                    var baseUrl = options.ServerUrl.EndsWith('/') ? options.ServerUrl : options.ServerUrl + "/";
                    http.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
                }
            })
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        var sp = services.BuildServiceProvider();

        var service = new WorkerHeartbeatService(
            sp.GetRequiredService<IHttpClientFactory>(),
            Options.Create(options),
            new StubJobDefinitionLookup(jobNames ?? []),
            time,
            NullLogger<WorkerHeartbeatService>.Instance);

        return new HeartbeatHarness(service, handler, time, sp);
    }

    public async ValueTask DisposeAsync()
    {
        await _sp.DisposeAsync().ConfigureAwait(false);
    }
}
