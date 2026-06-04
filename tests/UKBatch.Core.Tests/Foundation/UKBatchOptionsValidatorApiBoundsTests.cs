using FluentAssertions;
using UKBatch;
using Xunit;

namespace UKBatch.Core.Tests.Foundation;

/// <summary>
/// Validator coverage for the API/pagination/hub bounds — HubBufferCapacity, MaxPageLimit,
/// DefaultPageLimit, HubPath, and the /executions/query input limits.
/// </summary>
public class UKBatchOptionsValidatorApiBoundsTests
{
    private static UKBatchOptionsValidator Validator => new();

    [Fact]
    public void Validate_DefaultOptions_IncludesPaginationAndHubDefaults()
    {
        var opts = new UKBatchOptions();
        opts.HubBufferCapacity.Should().Be(256);
        opts.MaxPageLimit.Should().Be(500);
        opts.DefaultPageLimit.Should().Be(50);
        opts.HubPath.Should().Be("/hubs/jobs");
        Validator.Validate(null, opts).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_HubBufferCapacityZero_Fails()
    {
        var opts = new UKBatchOptions { HubBufferCapacity = 0 };
        Validator.Validate(null, opts).FailureMessage.Should().Contain("HubBufferCapacity");
    }

    [Fact]
    public void Validate_MaxPageLimitZero_Fails()
    {
        var opts = new UKBatchOptions { MaxPageLimit = 0 };
        Validator.Validate(null, opts).FailureMessage.Should().Contain("MaxPageLimit");
    }

    [Fact]
    public void Validate_DefaultPageLimitZero_Fails()
    {
        var opts = new UKBatchOptions { DefaultPageLimit = 0 };
        Validator.Validate(null, opts).FailureMessage.Should().Contain("DefaultPageLimit");
    }

    [Fact]
    public void Validator_RejectsConfigWhereDefault_ExceedsMax()
    {
        // Cross-field rule: DefaultPageLimit must not exceed MaxPageLimit.
        var opts = new UKBatchOptions { MaxPageLimit = 100, DefaultPageLimit = 200 };
        Validator.Validate(null, opts).FailureMessage.Should().Contain("DefaultPageLimit");
    }

    [Fact]
    public void Validator_RejectsHubPath_WithoutLeadingSlash()
    {
        var opts = new UKBatchOptions { HubPath = "hubs/jobs" };
        Validator.Validate(null, opts).FailureMessage.Should().Contain("HubPath");
    }

    [Fact]
    public void Validator_RejectsHubPath_Whitespace()
    {
        var opts = new UKBatchOptions { HubPath = "   " };
        Validator.Validate(null, opts).FailureMessage.Should().Contain("HubPath");
    }

    [Fact]
    public void Validator_RejectsHubPath_Empty()
    {
        var opts = new UKBatchOptions { HubPath = "" };
        Validator.Validate(null, opts).FailureMessage.Should().Contain("HubPath");
    }

    [Fact]
    public void Validate_MaxQueryStatusesCountZero_Fails()
    {
        // Input bounds for /executions/query.
        var opts = new UKBatchOptions { MaxQueryStatusesCount = 0 };
        Validator.Validate(null, opts).FailureMessage.Should().Contain("MaxQueryStatusesCount");
    }

    [Fact]
    public void Validate_MaxQuerySearchTextLengthZero_Fails()
    {
        var opts = new UKBatchOptions { MaxQuerySearchTextLength = 0 };
        Validator.Validate(null, opts).FailureMessage.Should().Contain("MaxQuerySearchTextLength");
    }

    [Fact]
    public void Validate_DefaultOptions_IncludeQueryInputBounds()
    {
        var opts = new UKBatchOptions();
        opts.MaxQueryStatusesCount.Should().Be(20);
        opts.MaxQuerySearchTextLength.Should().Be(1024);
    }
}
