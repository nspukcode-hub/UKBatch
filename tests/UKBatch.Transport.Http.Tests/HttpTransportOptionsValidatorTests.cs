using FluentAssertions;
using Microsoft.Extensions.Options;
using UKBatch.Transport.Http;
using Xunit;

namespace UKBatch.Transport.Http.Tests;

/// <summary>
/// <see cref="HttpTransportOptionsValidator"/> unit coverage for the body-size ceiling (the HMAC filter
/// buffers up to <c>MaxBodyBytes</c> BEFORE authenticating, so the value is a pre-auth memory ceiling)
/// plus the canonical happy path. Docker-free.
/// </summary>
[Trait("Category", "HttpTransport")]
public sealed class HttpTransportOptionsValidatorTests
{
    private static readonly HttpTransportOptionsValidator Validator = new();

    // Default DefaultRequestTimeout (30s) must exceed LongPollMaxWait + 5s slack, so lower the hold.
    private static HttpTransportOptions ValidBase() => new()
    {
        SharedSecret = new string('k', 32),
        LongPollMaxWait = TimeSpan.FromSeconds(20),
    };

    private static ValidateOptionsResult Validate(HttpTransportOptions options)
        => Validator.Validate(name: null, options);

    [Fact]
    public void Validate_ValidBase_Succeeds()
    {
        Validate(ValidBase()).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_MaxBodyBytesAboveCeiling_Fails()
    {
        var options = ValidBase();
        options.MaxBodyBytes = (16 * 1024 * 1024) + 1;
        var result = Validate(options);
        result.Succeeded.Should().BeFalse();
        result.Failures.Should().ContainMatch("*MaxBodyBytes must be in*");
    }

    [Fact]
    public void Validate_MaxBodyBytesAtCeiling_Succeeds()
    {
        var options = ValidBase();
        options.MaxBodyBytes = 16 * 1024 * 1024;
        Validate(options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_MaxBodyBytesZero_Fails()
    {
        var options = ValidBase();
        options.MaxBodyBytes = 0;
        Validate(options).Failures.Should().ContainMatch("*MaxBodyBytes must be in*");
    }
}
