using FluentAssertions;
using Xunit;

namespace UKBatch.Worker.Tests;

/// <summary>
/// <c>WorkerOptionsValidator</c>: WorkerName is required; when Heartbeat is enabled,
/// ServerUrl must be a valid absolute URI AND HeartbeatInterval strictly positive; Heartbeat=false
/// relaxes the ServerUrl/interval requirement.
/// </summary>
public sealed class WorkerOptionsValidatorTests
{
    private static readonly WorkerOptionsValidator Validator = new();

    private static WorkerOptions Valid() => new()
    {
        WorkerName = "invoicing",
        ServerUrl = "http://ukbatch-server:8080",
        Heartbeat = true,
        HeartbeatInterval = TimeSpan.FromSeconds(15),
    };

    [Fact]
    public void Validate_ValidConfig_Succeeds()
    {
        var result = Validator.Validate(name: null, Valid());
        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_BlankWorkerName_Fails(string workerName)
    {
        var opts = Valid();
        opts.WorkerName = workerName;

        var result = Validator.Validate(name: null, opts);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(WorkerOptions.WorkerName));
    }

    [Fact]
    public void Validate_HeartbeatTrue_NullServerUrl_Fails()
    {
        var opts = Valid();
        opts.ServerUrl = null;

        var result = Validator.Validate(name: null, opts);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(WorkerOptions.ServerUrl));
    }

    [Theory]
    [InlineData("relative")]            // no scheme, no leading slash → not absolute
    [InlineData("not a uri at all")]    // whitespace + no scheme → not absolute
    [InlineData("ftp:relative")]        // bare scheme without authority → not absolute
    public void Validate_HeartbeatTrue_RelativeServerUrl_Fails(string serverUrl)
    {
        // NOTE: a leading-slash path like "/foo" IS a valid absolute file:// URI per.NET's parser, so
        // it would (correctly) pass the validator — these cases are genuinely non-absolute.
        var opts = Valid();
        opts.ServerUrl = serverUrl;

        var result = Validator.Validate(name: null, opts);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(WorkerOptions.ServerUrl));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Validate_HeartbeatTrue_NonPositiveInterval_Fails(int seconds)
    {
        var opts = Valid();
        opts.HeartbeatInterval = TimeSpan.FromSeconds(seconds);

        var result = Validator.Validate(name: null, opts);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(WorkerOptions.HeartbeatInterval));
    }

    [Fact]
    public void Validate_HeartbeatFalse_RelaxesServerUrlAndInterval()
    {
        // Heartbeat disabled: the worker is dispatch-reachable but invisible in the panel, so the
        // heartbeat-only requirements (ServerUrl + positive interval) do NOT apply.
        var opts = new WorkerOptions
        {
            WorkerName = "invoicing",
            Heartbeat = false,
            ServerUrl = null,
            HeartbeatInterval = TimeSpan.Zero,
        };

        var result = Validator.Validate(name: null, opts);

        result.Succeeded.Should().BeTrue(
            "Heartbeat=false relaxes the ServerUrl/interval requirement (only WorkerName remains mandatory)");
    }

    [Fact]
    public void Validate_HeartbeatFalse_StillRequiresWorkerName()
    {
        var opts = new WorkerOptions { WorkerName = "", Heartbeat = false };

        var result = Validator.Validate(name: null, opts);

        result.Failed.Should().BeTrue("WorkerName is mandatory regardless of Heartbeat (it is the routing key)");
        result.FailureMessage.Should().Contain(nameof(WorkerOptions.WorkerName));
    }
}
