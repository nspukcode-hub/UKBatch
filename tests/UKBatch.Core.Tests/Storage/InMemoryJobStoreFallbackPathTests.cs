using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Runtime;
using UKBatch.Abstractions.Storage;
using UKBatch.Core.Tests.Helpers;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Core.Tests.Storage;

/// <summary>
/// gate — a non-<c>InMemoryJobStore</c> adapter that does
/// NOT implement <c>InsertAsync(JobExecution, CT)</c> falls back to <c>CreateAsync(JobDefinition)</c>
/// in <c>JobRunner.TriggerInternalAsync</c>; the runtime MUST emit a diagnostic <c>ILogger.LogWarning</c>
/// so EF/Redis adapter authors discover the gap at adapter test time.
/// </summary>
public class InMemoryJobStoreFallbackPathTests
{
    /// <summary>Test-only IJobStore that does NOT inherit from InMemoryJobStore.</summary>
    private sealed class DummyJobStore : IJobStore
    {
        private readonly Dictionary<string, JobExecution> _executions = new(StringComparer.Ordinal);

        public Task<JobExecution> CreateAsync(JobDefinition definition, CancellationToken cancellationToken)
        {
            var exec = new JobExecution
            {
                ExecutionId = $"dummy-{Guid.NewGuid():N}",
                JobName = definition.Name,
                Status = JobStatus.Pending,
                Parameters = definition.DefaultParameters,
                EnqueuedAtUtc = DateTimeOffset.UtcNow,
                AttemptNumber = 1,
                MaxRetries = definition.MaxRetries,
                Processed = 0,
                Failed = 0,
            };
            _executions[exec.ExecutionId] = exec;
            return Task.FromResult(exec);
        }

        public Task UpdateStatusAsync(string executionId, JobStatus status, string? errorMessage, CancellationToken cancellationToken)
        {
            if (_executions.TryGetValue(executionId, out var ex))
            {
                _executions[executionId] = ex with { Status = status };
            }
            return Task.CompletedTask;
        }

        public Task RecordAttemptAsync(string executionId, int attemptNumber, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UpdateProgressAsync(string executionId, long processed, long failed, long? total, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<JobExecution?> GetAsync(string executionId, CancellationToken cancellationToken)
            => Task.FromResult(_executions.TryGetValue(executionId, out var ex) ? ex : null);
        public Task<IReadOnlyList<JobExecution>> QueryAsync(JobQuery query, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<JobExecution>>(_executions.Values.ToList());
        public Task<long> CountAsync(JobQuery query, CancellationToken cancellationToken)
            => Task.FromResult<long>(_executions.Count);
        public async IAsyncEnumerable<JobExecution> WatchAsync(WatchOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                lock (Messages)
                {
                    Messages.Add(formatter(state, exception));
                }
            }
        }
    }

    [Fact]
    public async Task JobStore_FallbackPath_RaisesWarning_OrFails()
    {
        // Reset the per-process one-shot guard so this test sees the warning regardless of test
        // ordering. The static `_warnedAdapterTypes` set is shared across the test process.
        JobRunner.ResetWarnedAdapterTypesForTesting();

        // Replace IJobStore with our DummyJobStore + intercept JobRunner ILogger to capture the warning.
        var listLogger = new ListLogger<JobRunner>();
        var host = await TestHostBuilder.StartAsync(
            b =>
            {
                b.AddJob<SucceedingJob>().Named("fallback.test");
            },
            services =>
            {
                // Replace IJobStore with DummyJobStore — bypasses InMemoryJobStore.
                var jobStoreDescriptor = services.First(s => s.ServiceType == typeof(IJobStore));
                services.Remove(jobStoreDescriptor);
                services.AddSingleton<IJobStore, DummyJobStore>();
                // Replace JobRunner ILogger.
                services.AddSingleton<ILogger<JobRunner>>(listLogger);
            }).ConfigureAwait(false);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var awaiter = host.Services.GetRequiredService<IJobExecutionAwaiter>();
            // Use the standalone trigger path (no batch); the runtime still goes through
            // TriggerInternalAsync which hits the fallback branch.
            var exec = await runner.TriggerAsync("fallback.test", UKBatch.Abstractions.Jobs.JobParameters.Empty, "test", default).ConfigureAwait(false);
            // Don't wait for terminal — the dummy store doesn't fan-out, so the waiter would hang.
            // The warning is emitted at trigger time, not at terminal.

            // Allow logger ordering to settle.
            await Task.Delay(50).ConfigureAwait(false);

            listLogger.Messages.Should().Contain(m => m.Contains("does not implement InsertAsync", StringComparison.Ordinal),
 "the runtime MUST log a diagnostic warning when adapter packages don't implement InsertAsync(JobExecution, CT).");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task JobStore_FallbackPath_WarnsOncePerAdapterType_NotPerCall()
    {
        // cleanup: the fallback warning is emitted ONCE per adapter
        // type per process. Without this guard, EF/Redis adapter authors who forget the
        // InsertAsync overload would generate N warnings per N executions (alert flood).
        JobRunner.ResetWarnedAdapterTypesForTesting();

        var listLogger = new ListLogger<JobRunner>();
        var host = await TestHostBuilder.StartAsync(
            b =>
            {
                b.AddJob<SucceedingJob>().Named("fallback.warn-once");
            },
            services =>
            {
                var jobStoreDescriptor = services.First(s => s.ServiceType == typeof(IJobStore));
                services.Remove(jobStoreDescriptor);
                services.AddSingleton<IJobStore, DummyJobStore>();
                services.AddSingleton<ILogger<JobRunner>>(listLogger);
            }).ConfigureAwait(false);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();

            // Trigger the same job N=5 times — should produce exactly ONE warning, not 5.
            for (var i = 0; i < 5; i++)
            {
                await runner.TriggerAsync("fallback.warn-once", UKBatch.Abstractions.Jobs.JobParameters.Empty, "test", default).ConfigureAwait(false);
            }

            await Task.Delay(50).ConfigureAwait(false);

            var fallbackWarnings = listLogger.Messages
                .Where(m => m.Contains("does not implement InsertAsync", StringComparison.Ordinal))
                .ToList();
            fallbackWarnings.Should().HaveCount(1,
 " cleanup: the fallback warning MUST emit exactly once per adapter type per process — N=5 triggers must produce 1 warning, not 5. This prevents production alert flooding.");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host).ConfigureAwait(false);
        }
    }
}
