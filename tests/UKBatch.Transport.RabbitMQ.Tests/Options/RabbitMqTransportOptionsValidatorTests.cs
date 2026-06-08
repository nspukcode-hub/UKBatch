using FluentAssertions;
using Microsoft.Extensions.Options;
using UKBatch.Transport.RabbitMQ;
using Xunit;

namespace UKBatch.Transport.RabbitMQ.Tests.Options;

/// <summary>
/// <see cref="RabbitMqTransportOptionsValidator"/> unit coverage. Locks the
/// invariant (<c>ConsumerDispatchConcurrency</c> MUST be 1 in v0.1), the Uri XOR
/// discrete-fields rule, and every numeric / name guard. Docker-free.
/// </summary>
public sealed class RabbitMqTransportOptionsValidatorTests
{
    private static readonly RabbitMqTransportOptionsValidator Validator = new();

    private static ValidateOptionsResult Validate(RabbitMqTransportOptions options)
        => Validator.Validate(name: null, options);

    // ===== Default-construct is valid (the canonical happy path) =====

    [Fact]
    public void Validate_DefaultOptions_Succeeds()
    {
        var result = Validate(new RabbitMqTransportOptions());
        result.Succeeded.Should().BeTrue(result.FailureMessage);
    }

    [Fact]
    public void Validate_NullOptions_Throws()
    {
        var act = () => Validate(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ===== ConsumerDispatchConcurrency MUST be 1 =====

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)2)]
    [InlineData((ushort)16)]
    [InlineData((ushort)255)]
    public void Validate_ConsumerDispatchConcurrencyNotOne_Fails(ushort concurrency)
    {
        var options = new RabbitMqTransportOptions { ConsumerDispatchConcurrency = concurrency };
        var result = Validate(options);
        result.Succeeded.Should().BeFalse();
        result.Failures.Should().ContainMatch("*ConsumerDispatchConcurrency must be 1*");
    }

    [Fact]
    public void Validate_ConsumerDispatchConcurrencyOne_Succeeds()
    {
        var options = new RabbitMqTransportOptions { ConsumerDispatchConcurrency = 1 };
        Validate(options).Succeeded.Should().BeTrue();
    }

    // ===== Uri XOR discrete fields =====

    [Theory]
    [InlineData("amqp://user:pass@host:5672/vhost")]
    [InlineData("amqps://user:pass@host:5671/")]
    [InlineData("amqp://localhost")]
    public void Validate_UriAloneWithDefaults_Succeeds(string uri)
    {
        // Uri set + all discrete fields at default → unambiguous.
        var options = new RabbitMqTransportOptions { Uri = uri };
        Validate(options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_UriWithNonDefaultHostName_FailsAmbiguous()
    {
        var options = new RabbitMqTransportOptions { Uri = "amqp://host", HostName = "other-host" };
        var result = Validate(options);
        result.Succeeded.Should().BeFalse();
        result.Failures.Should().ContainMatch("*Uri is set together with non-default discrete connection fields*");
    }

    [Fact]
    public void Validate_UriWithNonDefaultPort_FailsAmbiguous()
    {
        var options = new RabbitMqTransportOptions { Uri = "amqp://host", Port = 5673 };
        Validate(options).Failures.Should().ContainMatch("*Uri is set together with non-default discrete*");
    }

    [Fact]
    public void Validate_UriWithNonDefaultUserName_FailsAmbiguous()
    {
        var options = new RabbitMqTransportOptions { Uri = "amqp://host", UserName = "admin" };
        Validate(options).Failures.Should().ContainMatch("*Uri is set together with non-default discrete*");
    }

    [Fact]
    public void Validate_UriWithUseTls_FailsAmbiguous()
    {
        var options = new RabbitMqTransportOptions { Uri = "amqp://host", UseTls = true };
        Validate(options).Failures.Should().ContainMatch("*Uri is set together with non-default discrete*");
    }

    [Theory]
    [InlineData("not-a-uri")]
    [InlineData("http://host")]
    [InlineData("ftp://host")]
    [InlineData("host:5672")]
    public void Validate_InvalidUriScheme_Fails(string uri)
    {
        var options = new RabbitMqTransportOptions { Uri = uri };
        var result = Validate(options);
        result.Succeeded.Should().BeFalse();
        result.Failures.Should().ContainMatch("*must be an absolute amqp:// or amqps:// URI*");
    }

    // ===== Discrete-field connection validation (Uri not set) =====

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptyHostNameWithoutUri_Fails(string hostName)
    {
        var options = new RabbitMqTransportOptions { HostName = hostName };
        Validate(options).Failures.Should().ContainMatch("*HostName is required when Uri is not set*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(100000)]
    public void Validate_PortOutOfRange_Fails(int port)
    {
        var options = new RabbitMqTransportOptions { Port = port };
        Validate(options).Failures.Should().ContainMatch("*Port must be in [1, 65535]*");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5672)]
    [InlineData(65535)]
    public void Validate_PortInRange_Succeeds(int port)
    {
        var options = new RabbitMqTransportOptions { Port = port };
        Validate(options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_EmptyVirtualHostWithoutUri_Fails()
    {
        var options = new RabbitMqTransportOptions { VirtualHost = "" };
        Validate(options).Failures.Should().ContainMatch("*VirtualHost is required when Uri is not set*");
    }

    [Fact]
    public void Validate_EmptyUserNameWithoutUri_Fails()
    {
        var options = new RabbitMqTransportOptions { UserName = "" };
        Validate(options).Failures.Should().ContainMatch("*UserName is required when Uri is not set*");
    }

    // ===== Topology name guards =====

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptyExchangeName_Fails(string name)
    {
        var options = new RabbitMqTransportOptions { ExchangeName = name };
        Validate(options).Failures.Should().ContainMatch("*ExchangeName is required*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptyDeadLetterExchangeName_Fails(string name)
    {
        var options = new RabbitMqTransportOptions { DeadLetterExchangeName = name };
        Validate(options).Failures.Should().ContainMatch("*DeadLetterExchangeName is required*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptyDeadLetterQueueName_Fails(string name)
    {
        var options = new RabbitMqTransportOptions { DeadLetterQueueName = name };
        Validate(options).Failures.Should().ContainMatch("*DeadLetterQueueName is required*");
    }

    [Fact]
    public void Validate_EmptyQueuePrefix_Succeeds()
    {
        // QueuePrefix may legitimately be empty (queue name == service name).
        var options = new RabbitMqTransportOptions { QueuePrefix = "" };
        Validate(options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhitespaceQueuePrefix_Fails()
    {
        var options = new RabbitMqTransportOptions { QueuePrefix = "   " };
        Validate(options).Failures.Should().ContainMatch("*QueuePrefix must not be whitespace-only*");
    }

    // ===== Behavior numeric guards =====

    [Fact]
    public void Validate_ZeroPrefetchCount_Fails()
    {
        var options = new RabbitMqTransportOptions { PrefetchCount = 0 };
        Validate(options).Failures.Should().ContainMatch("*PrefetchCount must be >= 1*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Validate_MaxRedeliveryCountBelowOne_Fails(int count)
    {
        var options = new RabbitMqTransportOptions { MaxRedeliveryCount = count };
        Validate(options).Failures.Should().ContainMatch("*MaxRedeliveryCount must be >= 1*");
    }

    [Fact]
    public void Validate_NonPositiveRequestTimeout_Fails()
    {
        var options = new RabbitMqTransportOptions { DefaultRequestTimeout = TimeSpan.Zero };
        Validate(options).Failures.Should().ContainMatch("*DefaultRequestTimeout must be in (0, 10 min]*");
    }

    [Fact]
    public void Validate_RequestTimeoutTooLarge_Fails()
    {
        var options = new RabbitMqTransportOptions { DefaultRequestTimeout = TimeSpan.FromMinutes(11) };
        Validate(options).Failures.Should().ContainMatch("*DefaultRequestTimeout must be in (0, 10 min]*");
    }

    [Fact]
    public void Validate_NonPositivePublisherConfirmTimeout_Fails()
    {
        var options = new RabbitMqTransportOptions { PublisherConfirmTimeout = TimeSpan.FromSeconds(-1) };
        Validate(options).Failures.Should().ContainMatch("*PublisherConfirmTimeout must be in (0, 5 min]*");
    }

    [Fact]
    public void Validate_PublisherConfirmTimeoutTooLarge_Fails()
    {
        var options = new RabbitMqTransportOptions { PublisherConfirmTimeout = TimeSpan.FromMinutes(6) };
        Validate(options).Failures.Should().ContainMatch("*PublisherConfirmTimeout must be in (0, 5 min]*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    public void Validate_MessageIdCacheCapacityBelowMinimum_Fails(int capacity)
    {
        var options = new RabbitMqTransportOptions { MessageIdCacheCapacity = capacity };
        Validate(options).Failures.Should().ContainMatch("*MessageIdCacheCapacity must be >= 16*");
    }

    [Fact]
    public void Validate_MessageIdCacheCapacityAtMinimum_Succeeds()
    {
        var options = new RabbitMqTransportOptions { MessageIdCacheCapacity = 16 };
        Validate(options).Succeeded.Should().BeTrue();
    }

    // ===== Resilience guards =====

    [Fact]
    public void Validate_NonPositiveRetryDelay_Fails()
    {
        var options = new RabbitMqTransportOptions
        {
            RetryDelays = new[] { TimeSpan.FromSeconds(2), TimeSpan.Zero, TimeSpan.FromSeconds(5) },
        };
        Validate(options).Failures.Should().ContainMatch("*RetryDelays[1] must be positive*");
    }

    [Fact]
    public void Validate_PositiveRetryDelays_Succeeds()
    {
        var options = new RabbitMqTransportOptions
        {
            RetryDelays = new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) },
        };
        Validate(options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_NullRetryDelays_Succeeds()
    {
        // null => default schedule, not a validation failure.
        var options = new RabbitMqTransportOptions { RetryDelays = null };
        Validate(options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_CircuitBreakerThresholdBelowOne_Fails()
    {
        var options = new RabbitMqTransportOptions { CircuitBreakerThreshold = 0 };
        Validate(options).Failures.Should().ContainMatch("*CircuitBreakerThreshold must be >= 1*");
    }

    [Fact]
    public void Validate_NonPositiveCircuitBreakerWindow_Fails()
    {
        var options = new RabbitMqTransportOptions { CircuitBreakerWindow = TimeSpan.Zero };
        Validate(options).Failures.Should().ContainMatch("*CircuitBreakerWindow must be positive*");
    }

    // ===== Multiple failures accumulate =====

    [Fact]
    public void Validate_MultipleViolations_ReportsAllOfThem()
    {
        var options = new RabbitMqTransportOptions
        {
            PrefetchCount = 0,
            MaxRedeliveryCount = 0,
            ConsumerDispatchConcurrency = 4,
            ExchangeName = "",
        };
        var result = Validate(options);
        result.Succeeded.Should().BeFalse();
        result.Failures.Should().HaveCountGreaterThanOrEqualTo(4);
    }

    [Fact]
    public void Validate_WiredThroughOptionsValidation_FailsHostStart()
    {
        // Sanity: the validator is the IValidateOptions<T> the host invokes.
        IValidateOptions<RabbitMqTransportOptions> validator = new RabbitMqTransportOptionsValidator();
        var bad = new RabbitMqTransportOptions { ConsumerDispatchConcurrency = 8 };
        validator.Validate(Microsoft.Extensions.Options.Options.DefaultName, bad)
            .Succeeded.Should().BeFalse();
    }

    // ===== Insecure-broker guard: default guest/guest on a non-loopback host =====

    [Fact]
    public void Validate_NonLoopbackHostWithGuestDefault_Fails()
    {
        var options = new RabbitMqTransportOptions { HostName = "broker.internal" };
        var result = Validate(options);
        result.Succeeded.Should().BeFalse();
        result.Failures.Should().ContainMatch("*default*guest/guest credentials*");
    }

    [Fact]
    public void Validate_NonLoopbackHostWithGuestDefault_AllowInsecureBroker_Succeeds()
    {
        var options = new RabbitMqTransportOptions { HostName = "broker.internal", AllowInsecureBroker = true };
        Validate(options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_NonLoopbackHostWithDedicatedUser_Succeeds()
    {
        var options = new RabbitMqTransportOptions
        {
            HostName = "broker.internal",
            UserName = "appuser",
            Password = "s3cret",
        };
        Validate(options).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void Validate_LoopbackHostWithGuestDefault_Succeeds(string host)
    {
        // Loopback brokers (local dev / same host) are exempt even with the guest/guest default.
        var options = new RabbitMqTransportOptions { HostName = host };
        Validate(options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_UriWithGuestOnNonLoopback_Fails()
    {
        var options = new RabbitMqTransportOptions { Uri = "amqp://guest:guest@broker.internal:5672/" };
        Validate(options).Failures.Should().ContainMatch("*default*guest/guest credentials*");
    }

    [Fact]
    public void Validate_UriWithDedicatedUserOnNonLoopback_Succeeds()
    {
        var options = new RabbitMqTransportOptions { Uri = "amqps://appuser:s3cret@broker.internal:5671/" };
        Validate(options).Succeeded.Should().BeTrue();
    }
}
