using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using UKBatch;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Runtime;
using UKBatch.Abstractions.Transport;
using UKBatch.Builders;
using UKBatch.Transport.RabbitMQ;
using UKBatch.Transport.RabbitMQ.Connection;
using UKBatch.Transport.RabbitMQ.Receiver;

namespace UKBatch.Transport.RabbitMQ.Tests.Integration;

/// <summary>
/// Integration-test helpers: real worker hosts (consumer pump + core runtime over a live broker) and
/// sender transports, plus broker queue-inspection utilities. Each test passes a UNIQUE
/// <c>topologyPrefix</c> so its exchange / DLX / DLQ / service queue are fully isolated inside the
/// shared container.
/// </summary>
internal static class RabbitMqTestHarness
{
    // ===== Test jobs =====

    /// <summary>Always completes (the happy path).</summary>
    internal sealed class CompletingJob : IJob
    {
        public static int RunCount;

        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RunCount);
            return Task.CompletedTask;
        }
    }

    /// <summary>Always throws → the runtime drives the execution to <see cref="JobStatus.Failed"/>.</summary>
    internal sealed class FailingJob : IJob
    {
        public static int RunCount;

        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RunCount);
            throw new InvalidOperationException("FailingJob intentional failure.");
        }
    }

    /// <summary>
    /// Records each run and signals a <see cref="TaskCompletionSource"/> after the configured number of
    /// runs — lets durability / dedupe tests await "the job actually ran exactly N times".
    /// </summary>
    internal sealed class CountingJob : IJob
    {
        public static int RunCount;
        public static TaskCompletionSource RanOnce { get; private set; } = NewTcs();

        public static void Reset()
        {
            Interlocked.Exchange(ref RunCount, 0);
            RanOnce = NewTcs();
        }

        private static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RunCount);
            RanOnce.TrySetResult();
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// An <see cref="IJobExecutionAwaiter"/> whose <c>WaitForTerminalAsync</c> throws a non-OCE — injected
    /// to simulate an UNEXPECTED failure in steps 4–9 of the pump AFTER the dedupe <c>TryAdd</c> succeeded
    /// (the path: must evict the dedupe key + dead-letter so redelivery re-runs).
    /// </summary>
    internal sealed class FaultingAwaiter : IJobExecutionAwaiter
    {
        public int CallCount;

        public Task<JobExecution> WaitForTerminalAsync(string executionId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CallCount);
            throw new InvalidOperationException("FaultingAwaiter: simulated unexpected processing failure (post-dedupe).");
        }

        public void CancelWaiter(string executionId)
        {
        }
    }

    /// <summary>
    /// Minimal <see cref="IHostApplicationLifetime"/> for the bare-<see cref="ServiceCollection"/> worker
    /// host (the core <c>UKBatchHost</c> + <c>JobRunner</c> capture it; a full Generic Host is overkill for
    /// these transport tests). <c>ApplicationStopping</c> never fires during a test.
    /// </summary>
    private sealed class TestHostLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
        public void Dispose() => _stopping.Dispose();
    }

    // ===== Worker host (consumer pump + runtime) =====

    /// <summary>
    /// A started worker host: the core runtime plus a running <see cref="RabbitMqConsumerPump"/> consuming
    /// the service queue. Dispose stops the pump and tears the connection down.
    /// </summary>
    internal sealed class WorkerHost : IAsyncDisposable
    {
        private readonly ServiceProvider _sp;
        private readonly IReadOnlyList<IHostedService> _hostedServices;

        private WorkerHost(ServiceProvider sp, IReadOnlyList<IHostedService> hostedServices)
        {
            _sp = sp;
            _hostedServices = hostedServices;
        }

        public IServiceProvider Services => _sp;

        public static async Task<WorkerHost> StartAsync(
            string connectionUri,
            string serviceName,
            string topologyPrefix,
            Action<UKBatchBuilder>? configureJobs = null,
            bool faultingAwaiter = false,
            int maxRedeliveryCount = 5,
            CancellationToken cancellationToken = default)
        {
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
            services.AddSingleton<IHostApplicationLifetime, TestHostLifetime>();
            services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

            services.AddUKBatch(b =>
            {
                b.AddJob<CompletingJob>().Named(nameof(CompletingJob));
                b.AddJob<FailingJob>().Named(nameof(FailingJob));
                b.AddJob<CountingJob>().Named(nameof(CountingJob));
                configureJobs?.Invoke(b);
            });
            services.Configure<UKBatchOptions>(o => o.ThisServiceName = serviceName);

            services.AddUKBatchRabbitMqTransport(o =>
            {
                o.Uri = connectionUri;
                o.ExchangeName = topologyPrefix + ".jobs";
                o.DeadLetterExchangeName = topologyPrefix + ".jobs.dlx";
                o.DeadLetterQueueName = topologyPrefix + ".dlq";
                o.QueuePrefix = topologyPrefix + ".service.";
                o.MaxRedeliveryCount = maxRedeliveryCount;
            });

            if (faultingAwaiter)
            {
                services.RemoveAll<IJobExecutionAwaiter>();
                services.AddSingleton<IJobExecutionAwaiter, FaultingAwaiter>();
            }

            var sp = services.BuildServiceProvider();

            // Start ALL hosted services, with the core UKBatchHost (dispatcher + awaiter + scheduler)
            // BEFORE the consumer pump — the pump's WaitForTerminalAsync needs the awaiter running and the
            // job needs the dispatcher running. Order: non-pump hosted services first, pump last.
            var hostedServices = sp.GetServices<IHostedService>().ToList();
            var ordered = hostedServices
                .OrderBy(h => h is RabbitMqConsumerPump ? 1 : 0)
                .ToList();
            foreach (var hosted in ordered)
            {
                await hosted.StartAsync(cancellationToken).ConfigureAwait(false);
            }

            return new WorkerHost(sp, ordered);
        }

        public async ValueTask DisposeAsync()
        {
            // Stop in reverse start order (pump first, then the core host).
            foreach (var hosted in _hostedServices.Reverse())
            {
                try
                {
                    await hosted.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // best-effort
                }
            }
            await _sp.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ===== Sender (publish + RPC) =====

    /// <summary>A standalone sender: a <see cref="RabbitMqTransport"/> with NO service queue (orchestrator-only).</summary>
    internal sealed class Sender : IAsyncDisposable
    {
        private readonly ServiceProvider _sp;

        private Sender(ServiceProvider sp) => _sp = sp;

        public RabbitMqTransport Transport => _sp.GetRequiredService<RabbitMqTransport>();

        public static Sender Build(string connectionUri, string topologyPrefix, string senderName = "orchestrator")
        {
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
            services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
            // No AddUKBatch → no core runtime; this node is publish/RPC only. ThisServiceName names the
            // AMQP client connection but the sender never declares/consumes a service queue.
            services.Configure<UKBatchOptions>(o => o.ThisServiceName = senderName);
            services.AddUKBatchRabbitMqTransport(o =>
            {
                o.Uri = connectionUri;
                o.ExchangeName = topologyPrefix + ".jobs";
                o.DeadLetterExchangeName = topologyPrefix + ".jobs.dlx";
                o.DeadLetterQueueName = topologyPrefix + ".dlq";
                o.QueuePrefix = topologyPrefix + ".service.";
            });
            // The sender needs core types referenced by the transport graph? RabbitMqTransport depends only
            // on the connection manager + reply router + logger — all registered by AddUKBatchRabbitMqTransport.
            return new Sender(services.BuildServiceProvider());
        }

        public async ValueTask DisposeAsync() => await _sp.DisposeAsync().ConfigureAwait(false);
    }

    // ===== Broker inspection =====

    /// <summary>
    /// Opens a raw inspection connection/channel to the broker for asserting queue depths and draining
    /// the DLQ. Dispose closes it.
    /// </summary>
    internal sealed class BrokerInspector : IAsyncDisposable
    {
        private readonly IConnection _connection;
        private readonly IChannel _channel;

        private BrokerInspector(IConnection connection, IChannel channel)
        {
            _connection = connection;
            _channel = channel;
        }

        public static async Task<BrokerInspector> ConnectAsync(string connectionUri)
        {
            var factory = new ConnectionFactory { Uri = new Uri(connectionUri) };
            var connection = await factory.CreateConnectionAsync("inspector").ConfigureAwait(false);
            var channel = await connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: false, publisherConfirmationTrackingEnabled: false))
                .ConfigureAwait(false);
            return new BrokerInspector(connection, channel);
        }

        /// <summary>Ready-message count for a queue (passive — does not redeclare).</summary>
        public async Task<uint> MessageCountAsync(string queue)
            => await _channel.MessageCountAsync(queue, CancellationToken.None).ConfigureAwait(false);

        /// <summary>
        /// Polls a queue's ready-message count until it equals <paramref name="expected"/> or the timeout
        /// elapses, returning the last observed value. Tolerates the broker's eventual-consistency lag on
        /// quorum-queue counters after an ack/nack.
        /// </summary>
        public async Task<uint> WaitForMessageCountAsync(string queue, uint expected, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            uint last = 0;
            while (DateTime.UtcNow < deadline)
            {
                last = await MessageCountAsync(queue).ConfigureAwait(false);
                if (last == expected)
                {
                    return last;
                }
                await Task.Delay(100).ConfigureAwait(false);
            }
            return last;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _channel.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                await _channel.DisposeAsync().ConfigureAwait(false);
                await _connection.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                await _connection.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // best-effort
            }
        }
    }

    // ===== Message factory =====

    internal static JobMessage Message(
        string jobName,
        string targetService,
        string? messageId = null,
        string sourceService = "orchestrator",
        IReadOnlyDictionary<string, object?>? parameters = null)
        => new()
        {
            MessageId = messageId ?? Guid.NewGuid().ToString("N"),
            CorrelationId = null,
            JobName = jobName,
            SourceService = sourceService,
            TargetService = targetService,
            BatchId = null,
            BatchStepId = null,
            Parameters = parameters ?? new Dictionary<string, object?>(),
            Headers = new Dictionary<string, string>(),
            EnqueuedAtUtc = DateTimeOffset.UtcNow,
            AttemptNumber = 1,
        };

    /// <summary>A short, unique topology prefix for per-test isolation inside the shared container.</summary>
    internal static string NewTopologyPrefix() => "t" + Guid.NewGuid().ToString("N")[..12];
}
