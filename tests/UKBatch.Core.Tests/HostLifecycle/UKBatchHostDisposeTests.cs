using FluentAssertions;
using Microsoft.Extensions.Hosting;
using UKBatch.Core.Tests.Helpers;
using Xunit;

namespace UKBatch.Core.Tests.HostLifecycle;

/// <summary>
/// Locks down the fix for the double-DisposeAsync chain. The generic Host disposes singletons
/// (including <c>JobExecutionAwaiter</c> and <c>DebouncedProgressFlusher</c>) directly; the
/// <c>UKBatchHost.DisposeAsync</c> body MUST NOT re-dispose them — its sole job is to release the
/// linked stopping CTS.
/// </summary>
public class UKBatchHostDisposeTests
{
    [Fact]
    public async Task DisposingHostAfterStop_DoesNotThrow()
    {
        var threw = false;
        try
        {
            var host = await TestHostBuilder.StartAsync().ConfigureAwait(false);
            await host.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            host.Dispose();
        }
        catch (ObjectDisposedException)
        {
            threw = true;
        }
        catch (AggregateException ag) when (ag.InnerExceptions.OfType<ObjectDisposedException>().Any())
        {
            threw = true;
        }

        threw.Should().BeFalse("the DI container owns singleton disposal; UKBatchHost.DisposeAsync must not redispose JobExecutionAwaiter / DebouncedProgressFlusher.");
    }

    [Fact]
    public async Task DisposingHostWithoutStop_DoesNotThrow()
    {
        // Some apps short-circuit: build host, then Dispose without StopAsync. The host's
        // DisposeAsync must remain safe in that path too.
        var threw = false;
        try
        {
            var host = await TestHostBuilder.StartAsync().ConfigureAwait(false);
            host.Dispose();
        }
        catch (ObjectDisposedException)
        {
            threw = true;
        }
        catch (AggregateException ag) when (ag.InnerExceptions.OfType<ObjectDisposedException>().Any())
        {
            threw = true;
        }

        threw.Should().BeFalse();
    }
}
