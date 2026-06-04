using Testcontainers.RabbitMq;
using Xunit;

namespace UKBatch.Transport.RabbitMQ.Tests.Integration;

/// <summary>
/// Shared RabbitMQ Testcontainers fixture (one broker container per test class via
/// <c>IClassFixture&lt;RabbitMqContainerFixture&gt;</c>). Consuming classes carry
/// <c>[Trait("Category","RequiresDocker")]</c> so the Docker-free CI path filters them out
/// (<c>--filter Category!=RequiresDocker</c>). The image is pinned to a quorum-capable +
/// management build (<c>x-queue-type=quorum</c> requires RabbitMQ ≥ 3.8). Per-test isolation is
/// achieved by each test using a UNIQUE topology prefix (own exchange/DLX/DLQ/service queue) inside the
/// single shared container — the container itself starts once and tears down after the class.
/// </summary>
public sealed class RabbitMqContainerFixture : IAsyncLifetime
{
    private readonly RabbitMqContainer _container = new RabbitMqBuilder()
        .WithImage("rabbitmq:3.13-management")
        .Build();

    /// <summary>AMQP connection URI to the container (<c>amqp://guest:guest@host:port</c>).</summary>
    public string ConnectionUri => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();
}
