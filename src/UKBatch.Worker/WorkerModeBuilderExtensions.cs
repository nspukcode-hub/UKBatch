using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UKBatch.Builders;

namespace UKBatch.Worker;

/// <summary>
/// Registration entry point that turns the current host into a UKBatch worker
/// (Server + Workers deployment).
/// </summary>
public static class WorkerModeBuilderExtensions
{
    /// <summary>
    /// Configures this host as a worker:
    /// <list type="number">
    ///   <item>sets <c>UKBatchOptions.ThisServiceName</c> = <see cref="WorkerOptions.WorkerName"/> so
    ///   outbound cross-service messages (<c>JobMessage.SourceService</c>) are stamped;</item>
    ///   <item>registers the optional HTTP heartbeat (observability only — NEVER dispatch-critical);</item>
    ///   <item>installs a startup-time guard that fail-fasts if NO cross-service transport is registered
    ///   (the effective <c>ITransport</c> is still InProcess / unregistered).</item>
    /// </list>
    /// </summary>
    /// <param name="builder">The UKBatch builder (owns <c>.Services</c>).</param>
    /// <param name="configure">
    /// Callback that mutates <see cref="WorkerOptions"/>. It MUST be SIDE-EFFECT-FREE: it is invoked
    /// twice — once eagerly here against a throwaway probe instance (to read
    /// <see cref="WorkerOptions.WorkerName"/> synchronously so <c>ThisServiceName</c> can be set), and once
    /// by the options pipeline at runtime to build the authoritative singleton. Pure property assignment
    /// is the only thing it should do (no I/O, no captured-state mutation, no logging).
    /// </param>
    /// <remarks>
    /// CALL ORDER: <c>AddUKBatchAspNetCore(b =&gt; b.UseWorkerMode(...))</c> first, then the transport
    /// registration (<c>AddUKBatchRabbitMqTransport</c> / <c>AddUKBatchHttpTransport</c>) on
    /// <c>builder.Services</c>. The guard runs at host <c>StartAsync</c>, NOT here, so the registration
    /// order between <see cref="UseWorkerMode"/> and the transport does not matter.
    /// </remarks>
    public static UKBatchBuilder UseWorkerMode(this UKBatchBuilder builder, Action<WorkerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        // Bind appsettings "UKBatch:Worker", then overlay the callback (callback wins).
        builder.Services
            .AddOptions<WorkerOptions>()
            .BindConfiguration("UKBatch:Worker")
            .Configure(configure);
        builder.Services.AddSingleton<IValidateOptions<WorkerOptions>, WorkerOptionsValidator>();

        // Resolve WorkerName eagerly to set ThisServiceName. The callback runs here against a THROWAWAY
        // probe ONLY to read WorkerName for the Configure(UKBatchOptions) below; the authoritative
        // WorkerOptions singleton is still built by the options pipeline at runtime. The callback
        // is therefore invoked twice and MUST be side-effect-free (documented on the parameter).
        var probe = new WorkerOptions();
        configure(probe);
        if (string.IsNullOrWhiteSpace(probe.WorkerName))
        {
            throw new InvalidOperationException(
                "UseWorkerMode requires WorkerOptions.WorkerName (non-whitespace).");
        }

        // (1) Worker identity -> ThisServiceName (resolution chain: this WINS over env/assembly).
        builder.Configure(o => o.ThisServiceName = probe.WorkerName);

        // (2) Heartbeat. Registered unconditionally; no-ops at runtime if Heartbeat=false. The named
        //     HttpClient base address is normalized to a trailing slash so PostAsJsonAsync("api/workers/beat")
        //     resolves correctly (HttpClient strips the last path segment of a non-slash-terminated base).
        builder.Services
            .AddHttpClient(WorkerHeartbeatService.HttpClientName)
            .ConfigureHttpClient((sp, http) =>
            {
                var opts = sp.GetRequiredService<IOptions<WorkerOptions>>().Value;
                if (!string.IsNullOrWhiteSpace(opts.ServerUrl))
                {
                    var baseUrl = opts.ServerUrl.EndsWith('/') ? opts.ServerUrl : opts.ServerUrl + "/";
                    http.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
                }
            });
        builder.Services.AddSingleton<WorkerHeartbeatService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<WorkerHeartbeatService>());

        // (3) Fail-fast guard hosted service — reads the EFFECTIVE ITransport at StartAsync.
        builder.Services.AddHostedService<WorkerTransportGuard>();

        return builder;
    }
}
