using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UKBatch.Transport.Http;
using Xunit;

namespace UKBatch.Transport.Http.Tests.Options;

/// <summary>
/// <see cref="HttpTransportOptions"/> binding + validator (rule table). Each rule
/// produces a single descriptive failure in the resulting <see cref="OptionsValidationException"/>.
/// </summary>
[Trait("Category", "HttpTransport")]
public sealed class ServiceDiscoveryTests
{
    [Fact]
    public void Options_DefaultValues_AreReasonable()
    {
        var opts = new HttpTransportOptions();
        opts.SharedSecret.Should().BeEmpty();
        opts.DefaultRequestTimeout.Should().Be(TimeSpan.FromSeconds(30));
        opts.LongPollMaxWait.Should().Be(TimeSpan.FromSeconds(30));
        opts.MaxClockSkew.Should().Be(TimeSpan.FromSeconds(300));
        opts.NonceCacheCapacity.Should().Be(1024);
        opts.MessageIdCacheCapacity.Should().Be(4096);
        opts.CircuitBreakerThreshold.Should().Be(5);
        opts.MaxBodyBytes.Should().Be(1_048_576);
        opts.RetryDelays.Should().BeNull();
        opts.Services.Should().BeEmpty();
    }

    [Fact]
    public void Validator_SharedSecretEmpty_Fails()
    {
        var v = new HttpTransportOptionsValidator();
        var opts = ValidOpts();
        opts.SharedSecret = string.Empty;
        var result = v.Validate(name: null, opts);
        result.Failed.Should().BeTrue();
        result.Failures!.Should().Contain(f => f.Contains("SharedSecret"));
    }

    [Fact]
    public void Validator_ServicesEmpty_Succeeds()
    {
        // Receiver-only nodes have no outbound targets — empty Services is explicitly valid.
        var v = new HttpTransportOptionsValidator();
        var opts = ValidOpts();
        opts.Services.Clear();
        v.Validate(null, opts).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validator_BaseUrlRelative_Fails()
    {
        var v = new HttpTransportOptionsValidator();
        var opts = ValidOpts();
        opts.Services["x"] = new ServiceEndpoint { BaseUrl = new Uri("/relative", UriKind.Relative) };
        var result = v.Validate(null, opts);
        result.Failed.Should().BeTrue();
        result.Failures!.Should().Contain(f => f.Contains("absolute"));
    }

    [Fact]
    public void Validator_NegativeRetryDelays_Fails()
    {
        var v = new HttpTransportOptionsValidator();
        var opts = ValidOpts();
        opts.RetryDelays = new[] { TimeSpan.FromSeconds(-1) };
        var result = v.Validate(null, opts);
        result.Failed.Should().BeTrue();
        result.Failures!.Should().Contain(f => f.Contains("RetryDelays"));
    }

    [Fact]
    public void Validator_LongPollExceedsTimeout_Fails()
    {
        var v = new HttpTransportOptionsValidator();
        var opts = ValidOpts();
        opts.DefaultRequestTimeout = TimeSpan.FromSeconds(10);
        opts.LongPollMaxWait = TimeSpan.FromSeconds(30);
        var result = v.Validate(null, opts);
        result.Failed.Should().BeTrue();
        result.Failures!.Should().Contain(f => f.Contains("LongPollMaxWait"));
    }

    [Fact]
    public void Validator_NonceCacheTooSmall_Fails()
    {
        var v = new HttpTransportOptionsValidator();
        var opts = ValidOpts();
        opts.NonceCacheCapacity = 1;
        v.Validate(null, opts).Failed.Should().BeTrue();
    }

    [Fact]
    public void Validator_ClockSkewOutOfRange_Fails()
    {
        var v = new HttpTransportOptionsValidator();
        var opts = ValidOpts();
        opts.MaxClockSkew = TimeSpan.FromHours(2);
        var result = v.Validate(null, opts);
        result.Failed.Should().BeTrue();
        result.Failures!.Should().Contain(f => f.Contains("MaxClockSkew"));
    }

    [Fact]
    public void Validator_EmptyServiceKey_Fails()
    {
        var v = new HttpTransportOptionsValidator();
        var opts = ValidOpts();
        opts.Services["  "] = new ServiceEndpoint { BaseUrl = new Uri("http://x") };
        var result = v.Validate(null, opts);
        result.Failed.Should().BeTrue();
        result.Failures!.Should().Contain(f => f.Contains("empty"));
    }

    [Fact]
    public void Options_Binding_FromConfiguration_ReadsAllFields()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UKBatch:Transport:Http:SharedSecret"] = "BOUND-SECRET",
                ["UKBatch:Transport:Http:DefaultRequestTimeout"] = "00:01:00",
                ["UKBatch:Transport:Http:LongPollMaxWait"] = "00:00:45",
                ["UKBatch:Transport:Http:MaxClockSkew"] = "00:10:00",
                ["UKBatch:Transport:Http:NonceCacheCapacity"] = "2048",
                ["UKBatch:Transport:Http:Services:billing:BaseUrl"] = "http://billing.example",
                ["UKBatch:Transport:Http:Services:billing:Tag"] = "prod",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddUKBatchHttpTransport();
        using var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<IOptions<HttpTransportOptions>>().Value;
        opts.SharedSecret.Should().Be("BOUND-SECRET");
        opts.DefaultRequestTimeout.Should().Be(TimeSpan.FromMinutes(1));
        opts.LongPollMaxWait.Should().Be(TimeSpan.FromSeconds(45));
        opts.MaxClockSkew.Should().Be(TimeSpan.FromMinutes(10));
        opts.NonceCacheCapacity.Should().Be(2048);
        opts.Services.Should().ContainKey("billing");
        opts.Services["billing"].BaseUrl.AbsoluteUri.Should().Be("http://billing.example/");
        opts.Services["billing"].Tag.Should().Be("prod");
    }

    private static HttpTransportOptions ValidOpts() => new()
    {
        SharedSecret = "GOOD-SECRET-32B+",
        DefaultRequestTimeout = TimeSpan.FromSeconds(60),
        LongPollMaxWait = TimeSpan.FromSeconds(30),
        MaxClockSkew = TimeSpan.FromMinutes(5),
        CircuitBreakerThreshold = 5,
        CircuitBreakerWindow = TimeSpan.FromSeconds(30),
        NonceCacheCapacity = 64,
        MessageIdCacheCapacity = 256,
        MaxBodyBytes = 1024,
    };
}
