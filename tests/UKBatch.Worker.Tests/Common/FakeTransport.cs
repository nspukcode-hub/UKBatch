using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Transport;

namespace UKBatch.Worker.Tests.Common;

/// <summary>
/// Minimal <see cref="ITransport"/> stand-in whose only relevant surface for the
/// <c>WorkerTransportGuard</c> tests is <see cref="Name"/>. A guard test registers this with
/// <c>Name="RabbitMQ"</c> to prove the happy path (guard passes when a cross-service transport is
/// present). The publish/reply members throw — they are never exercised by the guard.
/// </summary>
internal sealed class FakeTransport : ITransport
{
    public FakeTransport(string name) => Name = name;

    public string Name { get; }

    public Task PublishAsync(JobMessage message, CancellationToken cancellationToken)
        => throw new NotSupportedException("FakeTransport is name-only for the guard tests.");

    public IAsyncEnumerable<JobMessage> SubscribeAsync(string topic, CancellationToken cancellationToken)
        => throw new NotSupportedException("FakeTransport is name-only for the guard tests.");

    public Task<JobResult> RequestReplyAsync(string targetService, JobMessage message, TimeSpan timeout, CancellationToken cancellationToken)
        => throw new NotSupportedException("FakeTransport is name-only for the guard tests.");
}
