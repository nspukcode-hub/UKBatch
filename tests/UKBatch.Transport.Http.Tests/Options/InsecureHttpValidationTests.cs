using FluentAssertions;
using UKBatch.Transport.Http;
using Xunit;

namespace UKBatch.Transport.Http.Tests.Options;

/// <summary>
/// <see cref="HttpTransportOptionsValidator"/> cleartext-http enforcement. A service endpoint using
/// <c>http</c> on a non-loopback host is rejected unless <see cref="HttpTransportOptions.AllowInsecureHttp"/>
/// is set; loopback hosts and <c>https</c> are always accepted.
/// </summary>
[Trait("Category", "HttpTransport")]
public sealed class InsecureHttpValidationTests
{
    [Fact]
    public void Validate_NonLoopbackHttp_DefaultFlag_Fails()
    {
        var v = new HttpTransportOptionsValidator();
        var opts = ValidOpts();
        opts.Services["billing"] = new ServiceEndpoint { BaseUrl = new Uri("http://example.com") };
        var result = v.Validate(name: null, opts);
        result.Failed.Should().BeTrue();
        result.Failures!.Should().Contain(f => f.Contains("cleartext http"));
    }

    [Fact]
    public void Validate_NonLoopbackHttp_FlagTrue_Passes()
    {
        var v = new HttpTransportOptionsValidator();
        var opts = ValidOpts();
        opts.AllowInsecureHttp = true;
        opts.Services["billing"] = new ServiceEndpoint { BaseUrl = new Uri("http://example.com") };
        v.Validate(null, opts).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_LoopbackHttp_DefaultFlag_Passes()
    {
        var v = new HttpTransportOptionsValidator();
        var opts = ValidOpts();
        opts.Services["local"] = new ServiceEndpoint { BaseUrl = new Uri("http://localhost:5150") };
        opts.Services["ip"] = new ServiceEndpoint { BaseUrl = new Uri("http://127.0.0.1:9000") };
        v.Validate(null, opts).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_Https_AlwaysPasses()
    {
        var v = new HttpTransportOptionsValidator();
        var opts = ValidOpts();
        opts.Services["billing"] = new ServiceEndpoint { BaseUrl = new Uri("https://billing.example.com") };
        v.Validate(null, opts).Succeeded.Should().BeTrue();
    }

    private static HttpTransportOptions ValidOpts() => new()
    {
        SharedSecret = new string('x', 32),
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
