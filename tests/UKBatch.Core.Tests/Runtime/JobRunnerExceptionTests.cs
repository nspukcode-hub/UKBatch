using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using UKBatch.Abstractions.Jobs;
using UKBatch.Core.Tests.Helpers;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Core.Tests.Runtime;

/// <summary>
/// throw-site tests for <see cref="JobNotRegisteredException"/>
/// and <see cref="BatchDefinitionNotFoundException"/> from <see cref="IJobRunner"/>.
/// </summary>
public class JobRunnerExceptionTests
{
    [Fact]
    public async Task TriggerAsync_UnknownJob_ThrowsJobNotRegistered()
    {
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<SucceedingJob>().Named("known.job");
        }).ConfigureAwait(false);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var act = async () => await runner.TriggerAsync("unregistered.job", JobParameters.Empty, "test", default);

            var ex = await act.Should().ThrowAsync<JobNotRegisteredException>().ConfigureAwait(false);
            ex.Which.JobName.Should().Be("unregistered.job");
            ex.Which.Message.Should().Contain("unregistered.job");
            ex.Which.Should().BeAssignableTo<InvalidOperationException>("A17 zero-test-churn.");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task TriggerBatchAsync_UnknownDefinition_ThrowsBatchDefinitionNotFound()
    {
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<SucceedingJob>().Named("any.job");
        }).ConfigureAwait(false);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var act = async () => await runner.TriggerBatchAsync("missing-def-id", null, "test", default);

            var ex = await act.Should().ThrowAsync<BatchDefinitionNotFoundException>().ConfigureAwait(false);
            ex.Which.BatchDefinitionId.Should().Be("missing-def-id");
            ex.Which.Message.Should().Contain("missing-def-id");
            ex.Which.Should().BeAssignableTo<InvalidOperationException>("A17 zero-test-churn.");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host).ConfigureAwait(false);
        }
    }
}
