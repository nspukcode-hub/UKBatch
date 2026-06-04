using FluentAssertions;
using UKBatch.Dashboard.Configuration;
using Xunit;

namespace UKBatch.Dashboard.Tests.Configuration;

// <summary> — DashboardOptionsValidator matrix (7 tests).</summary>
public sealed class DashboardOptionsValidatorTests
{
    private static DashboardOptions ValidBaseOptions() => new()
    {
        Services =
        [
            new UKBatchServiceDescriptor
            {
                Name = "self",
                BaseUrl = new Uri("http://localhost:5000/api"),
            },
        ],
    };

    [Fact]
    public void Validator_EmptyServices_Fails()
    {
        var validator = new DashboardOptionsValidator();
        var opts = new DashboardOptions();
        var result = validator.Validate(null, opts);
        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(msg => msg.Contains("at least one", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_DuplicateName_Fails()
    {
        var validator = new DashboardOptionsValidator();
        var opts = ValidBaseOptions();
        opts.Services.Add(new UKBatchServiceDescriptor { Name = "self", BaseUrl = new Uri("http://localhost:5001/api") });
        var result = validator.Validate(null, opts);
        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(msg => msg.Contains("duplicated", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_NonKebabName_Fails()
    {
        var validator = new DashboardOptionsValidator();
        var opts = ValidBaseOptions();
        opts.Services[0] = new UKBatchServiceDescriptor { Name = "MyService", BaseUrl = new Uri("http://localhost:5000/api") };
        var result = validator.Validate(null, opts);
        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(msg => msg.Contains("kebab-case", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RelativeBaseUrl_Fails()
    {
        var validator = new DashboardOptionsValidator();
        var opts = ValidBaseOptions();
        opts.Services[0] = new UKBatchServiceDescriptor { Name = "self", BaseUrl = new Uri("/api", UriKind.Relative) };
        var result = validator.Validate(null, opts);
        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(msg => msg.Contains("absolute URI", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_NegativeHttpTimeout_Fails()
    {
        var validator = new DashboardOptionsValidator();
        var opts = ValidBaseOptions();
        opts.HttpTimeout = TimeSpan.FromSeconds(-1);
        var result = validator.Validate(null, opts);
        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(msg => msg.Contains("> 0", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_HubPathWithoutSlash_Fails()
    {
        var validator = new DashboardOptionsValidator();
        var opts = ValidBaseOptions();
        opts.Services[0] = new UKBatchServiceDescriptor { Name = "self", BaseUrl = new Uri("http://localhost:5000/api"), HubPath = "hubs/jobs" };
        var result = validator.Validate(null, opts);
        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(msg => msg.Contains("HubPath", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_Happy_Succeeds()
    {
        var validator = new DashboardOptionsValidator();
        var opts = ValidBaseOptions();
        var result = validator.Validate(null, opts);
        result.Succeeded.Should().BeTrue();
        result.Failures.Should().BeNullOrEmpty();
    }

    [Fact]
    public void Validator_NegativeReconnectDelay_Fails()
    {
        var validator = new DashboardOptionsValidator();
        var opts = ValidBaseOptions();
        opts.ReconnectDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(-1)];
        var result = validator.Validate(null, opts);
        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(msg => msg.Contains("ReconnectDelays[1]", StringComparison.Ordinal));
    }
}
