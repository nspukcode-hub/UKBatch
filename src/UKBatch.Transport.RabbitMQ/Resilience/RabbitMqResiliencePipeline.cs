using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace UKBatch.Transport.RabbitMQ.Resilience;

/// <summary>
/// Polly v8 resilience pipeline for the INITIAL broker connect. Wraps the first
/// <c>CreateConnectionAsync</c> so a broker that is slow to come up is retried with exponential backoff
/// + jitter, guarded by a circuit breaker. Once connected, RabbitMQ.Client's own
/// <c>AutomaticRecoveryEnabled</c> handles reconnects — this pipeline covers only the cold-start window.
/// </summary>
/// <remarks>
/// <para><b>Ordering:</b> <c>CircuitBreaker → Retry</c> (CB outermost) — the breaker observes each
/// connect outcome before retry decides to re-attempt, so sustained broker-down trips the breaker and
/// subsequent attempts fail fast (mirrors the HTTP transport's CB-outside-Retry rationale).</para>
/// <para><b>Non-generic pipeline:</b> the connect action returns no result; the pipeline handles all
/// exceptions (broker-unreachable surfaces as <c>BrokerUnreachableException</c> / socket errors).</para>
/// </remarks>
internal sealed class RabbitMqResiliencePipeline
{
    private static readonly TimeSpan[] DefaultDelays =
    {
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
    };

    private readonly ResiliencePipeline _pipeline;

    /// <summary>Builds the initial-connect pipeline from the transport options.</summary>
    public RabbitMqResiliencePipeline(RabbitMqTransportOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        var delays = options.RetryDelays is { Count: > 0 }
            ? options.RetryDelays
            : DefaultDelays;

        _pipeline = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                Name = "ukbatch-rabbitmq-connect-cb",
                FailureRatio = 1.0,
                MinimumThroughput = options.CircuitBreakerThreshold,
                SamplingDuration = options.CircuitBreakerWindow,
                BreakDuration = options.CircuitBreakerWindow,
            })
            .AddRetry(new RetryStrategyOptions
            {
                Name = "ukbatch-rabbitmq-connect-retry",
                MaxRetryAttempts = delays.Count,
                DelayGenerator = args =>
                {
                    var idx = Math.Min(args.AttemptNumber, delays.Count - 1);
                    var d = delays[idx];
                    var jitter = TimeSpan.FromMilliseconds(
                        d.TotalMilliseconds * 0.1 * Random.Shared.NextDouble());
                    return ValueTask.FromResult<TimeSpan?>(d + jitter);
                },
                OnRetry = args =>
                {
                    logger.LogWarning(
                        args.Outcome.Exception,
                        "RabbitMQ initial-connect retry attempt {Attempt} after {Delay}ms.",
                        args.AttemptNumber, args.RetryDelay.TotalMilliseconds);
                    return ValueTask.CompletedTask;
                },
            })
            .Build();
    }

    /// <summary>Executes the connect <paramref name="action"/> through the retry + circuit-breaker pipeline.</summary>
    public ValueTask ExecuteAsync(
        Func<CancellationToken, ValueTask> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        return _pipeline.ExecuteAsync(action, cancellationToken);
    }
}
