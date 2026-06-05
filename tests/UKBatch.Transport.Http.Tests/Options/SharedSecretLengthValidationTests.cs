using FluentAssertions;
using UKBatch.Transport.Http;
using Xunit;

namespace UKBatch.Transport.Http.Tests.Options;

/// <summary>
/// <see cref="HttpTransportOptionsValidator"/> minimum-length enforcement for the HMAC
/// <see cref="HttpTransportOptions.SharedSecret"/>. A secret shorter than 32 characters is rejected;
/// an empty secret keeps its own distinct "required" message.
/// </summary>
[Trait("Category", "HttpTransport")]
public sealed class SharedSecretLengthValidationTests
{
    [Fact]
    public void Validate_SecretBelow32_Fails_WithLengthMessage()
    {
        var v = new HttpTransportOptionsValidator();
        var opts = ValidOpts();
        opts.SharedSecret = new string('x', 31);
        var result = v.Validate(name: null, opts);
        result.Failed.Should().BeTrue();
        result.Failures!.Should().Contain(f => f.Contains("at least 32 characters"));
    }

    [Fact]
    public void Validate_SecretExactly32_Passes()
    {
        var v = new HttpTransportOptionsValidator();
        var opts = ValidOpts();
        opts.SharedSecret = new string('x', 32);
        v.Validate(null, opts).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_SecretEmpty_Fails_WithRequiredMessage()
    {
        var v = new HttpTransportOptionsValidator();
        var opts = ValidOpts();
        opts.SharedSecret = string.Empty;
        var result = v.Validate(null, opts);
        result.Failed.Should().BeTrue();
        result.Failures!.Should().Contain(f => f.Contains("required"));
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
