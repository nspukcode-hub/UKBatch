using FluentAssertions;
using UKBatch;
using Xunit;

namespace UKBatch.Core.Tests.Foundation;

/// <summary>
/// Verifies N7 validation: MaxDoP, capacity ratios, ShutdownTimeout, ProgressFlushInterval, etc.
/// </summary>
public class UKBatchOptionsValidatorTests
{
    [Fact]
    public void Validate_DefaultOptions_Succeeds()
    {
        var validator = new UKBatchOptionsValidator();
        var result = validator.Validate(null, new UKBatchOptions());
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_MaxDegreeOfParallelismZero_Fails()
    {
        var validator = new UKBatchOptionsValidator();
        var opts = new UKBatchOptions { MaxDegreeOfParallelism = 0 };
        var result = validator.Validate(null, opts);
        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("MaxDegreeOfParallelism");
    }

    [Fact]
    public void Validate_NegativeMaxDoP_Fails()
    {
        var validator = new UKBatchOptionsValidator();
        var opts = new UKBatchOptions { MaxDegreeOfParallelism = -1 };
        var result = validator.Validate(null, opts);
        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_DispatcherCapacityBelowMaxDoP_Fails()
    {
        var validator = new UKBatchOptionsValidator();
        var opts = new UKBatchOptions { MaxDegreeOfParallelism = 8, DispatcherChannelCapacity = 4 };
        var result = validator.Validate(null, opts);
        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("DispatcherChannelCapacity");
    }

    [Fact]
    public void Validate_DispatcherCapacityZero_AllowedAsAutoDefault()
    {
        var validator = new UKBatchOptionsValidator();
        var opts = new UKBatchOptions { MaxDegreeOfParallelism = 8, DispatcherChannelCapacity = 0 };
        var result = validator.Validate(null, opts);
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_NegativeShutdownTimeout_Fails()
    {
        var validator = new UKBatchOptionsValidator();
        var opts = new UKBatchOptions { ShutdownTimeout = TimeSpan.FromSeconds(-1) };
        var result = validator.Validate(null, opts);
        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("ShutdownTimeout");
    }

    [Fact]
    public void Validate_ProgressFlushIntervalZero_Fails()
    {
        var validator = new UKBatchOptionsValidator();
        var opts = new UKBatchOptions { ProgressFlushInterval = TimeSpan.Zero };
        var result = validator.Validate(null, opts);
        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("ProgressFlushInterval");
    }

    [Fact]
    public void Validate_NegativeDefaultMaxRetries_Fails()
    {
        var validator = new UKBatchOptionsValidator();
        var opts = new UKBatchOptions { DefaultMaxRetries = -1 };
        var result = validator.Validate(null, opts);
        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_NegativeDefaultTimeoutSeconds_Fails()
    {
        var validator = new UKBatchOptionsValidator();
        var opts = new UKBatchOptions { DefaultTimeoutSeconds = -1 };
        var result = validator.Validate(null, opts);
        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_PartitionWorkerCountZero_Fails()
    {
        var validator = new UKBatchOptionsValidator();
        var opts = new UKBatchOptions { DefaultPartitionWorkerCount = 0 };
        var result = validator.Validate(null, opts);
        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_WatchBufferCapacityZero_Fails()
    {
        var validator = new UKBatchOptionsValidator();
        var opts = new UKBatchOptions { WatchBufferCapacity = 0 };
        var result = validator.Validate(null, opts);
        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_MultipleFailures_ReturnsAllMessages()
    {
        var validator = new UKBatchOptionsValidator();
        var opts = new UKBatchOptions
        {
            MaxDegreeOfParallelism = 0,
            ShutdownTimeout = TimeSpan.FromSeconds(-1),
            ProgressFlushInterval = TimeSpan.Zero,
            DefaultMaxRetries = -1,
            DefaultPartitionWorkerCount = 0,
            WatchBufferCapacity = 0,
        };
        var result = validator.Validate(null, opts);
        result.Failed.Should().BeTrue();
        // Should have multiple distinct failure messages collated.
        result.Failures.Should().HaveCountGreaterThan(3);
    }
}
