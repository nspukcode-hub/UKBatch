using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UKBatch.Abstractions.Transport;

namespace UKBatch.Worker;

/// <summary>
/// Startup-time fail-fast: a worker (<see cref="WorkerModeBuilderExtensions.UseWorkerMode"/>)
/// is useless without a cross-service transport, because the orchestrator's
/// <c>ITransport.RequestReplyAsync</c> would have no path to this process. This guard throws a clear,
/// actionable <see cref="InvalidOperationException"/> at host <see cref="StartAsync"/> if no such
/// transport is registered.
/// </summary>
/// <remarks>
/// <para>
/// DETECTION SEAM: both <c>AddUKBatchHttpTransport</c> and <c>AddUKBatchRabbitMqTransport</c>
/// <c>Remove</c> the <c>InProcessTransport</c> descriptor and <c>Replace</c> <see cref="ITransport"/>
/// with their concrete adapter. So the single robust signal is the EFFECTIVE
/// <see cref="ITransport.Name"/> at runtime. We resolve it via
/// <see cref="ServiceProviderServiceExtensions.GetService{T}"/> (NOT constructor injection) so that the
/// "no transport registered at all" case (<c>null</c>) is surfaced as the SAME actionable worker-mode
/// error rather than a cryptic DI activation failure. <c>null</c> and
/// <see cref="ITransport.Name"/> == <c>"InProcess"</c> are treated identically.
/// </para>
/// </remarks>
internal sealed class WorkerTransportGuard : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly IOptions<WorkerOptions> _options;
    private readonly ILogger<WorkerTransportGuard> _logger;

    public WorkerTransportGuard(
        IServiceProvider services,
        IOptions<WorkerOptions> options,
        ILogger<WorkerTransportGuard> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Resolve the EFFECTIVE transport. null (unregistered) is treated the SAME as InProcess.
        var transport = _services.GetService<ITransport>();
        var transportName = transport?.Name;

        if (transport is null || string.Equals(transportName, "InProcess", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Worker '{_options.Value.WorkerName}' (UseWorkerMode) requires a cross-service transport, " +
                $"but the effective ITransport is {(transport is null ? "not registered" : "still InProcess")}. " +
                "Register one BEFORE building the host: " +
                "builder.Services.AddUKBatchRabbitMqTransport(...) OR builder.Services.AddUKBatchHttpTransport(...) " +
                "(+ app.MapUKBatchHttpTransport() for the HTTP receiver endpoints). " +
                "Without it, the orchestrator's RequestReplyAsync has no path to this worker.");
        }

        _logger.LogInformation(
            "Worker '{Worker}' transport guard OK: ITransport = {Transport}.",
            _options.Value.WorkerName, transportName);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
