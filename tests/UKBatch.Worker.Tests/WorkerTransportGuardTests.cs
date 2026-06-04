using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UKBatch.Abstractions.Transport;
using UKBatch.Worker.Tests.Common;
using Xunit;

namespace UKBatch.Worker.Tests;

/// <summary>
/// Fail-fast transport guard. At host <c>StartAsync</c> the guard resolves the
/// EFFECTIVE <see cref="ITransport"/> via <c>GetService</c> and throws a clear, actionable
/// <see cref="InvalidOperationException"/> (naming BOTH transport registration helpers) when the
/// transport is still InProcess OR is unregistered (<c>null</c> — treated identically). It SUCCEEDS
/// (no throw) once a real cross-service transport is registered.
/// </summary>
public sealed class WorkerTransportGuardTests
{
    private static WorkerTransportGuard BuildGuard(ITransport? transport)
    {
        var services = new ServiceCollection();
        if (transport is not null)
        {
            services.AddSingleton(transport);
        }

        var sp = services.BuildServiceProvider();
        var options = Options.Create(new WorkerOptions { WorkerName = "invoicing" });
        return new WorkerTransportGuard(sp, options, NullLogger<WorkerTransportGuard>.Instance);
    }

    [Fact]
    public async Task StartAsync_TransportIsInProcess_ThrowsNamingBothTransports()
    {
        var guard = BuildGuard(new FakeTransport("InProcess"));

        Func<Task> act = () => guard.StartAsync(CancellationToken.None);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("AddUKBatchRabbitMqTransport",
            "the guard must name the RabbitMQ registration helper so the operator knows the fix");
        ex.Which.Message.Should().Contain("AddUKBatchHttpTransport",
            "the guard must also name the HTTP registration helper");
        ex.Which.Message.Should().Contain("invoicing", "the message includes the worker name for context");
    }

    [Fact]
    public async Task StartAsync_TransportUnregistered_ThrowsNamingBothTransports()
    {
        // null (no ITransport at all) is treated the SAME as InProcess → same actionable error,
        // not a cryptic DI activation failure.
        var guard = BuildGuard(transport: null);

        Func<Task> act = () => guard.StartAsync(CancellationToken.None);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("AddUKBatchRabbitMqTransport");
        ex.Which.Message.Should().Contain("AddUKBatchHttpTransport");
    }

    [Fact]
    public async Task StartAsync_CrossServiceTransportRegistered_DoesNotThrow()
    {
        var guard = BuildGuard(new FakeTransport("RabbitMQ"));

        Func<Task> act = () => guard.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync(
            "a registered cross-service transport (Name != InProcess) satisfies the worker-mode guard");
    }

    [Fact]
    public async Task StopAsync_IsNoOp()
    {
        var guard = BuildGuard(new FakeTransport("RabbitMQ"));
        Func<Task> act = () => guard.StopAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }
}
