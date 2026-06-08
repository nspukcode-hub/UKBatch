using Microsoft.Extensions.Options;

namespace UKBatch.Transport.RabbitMQ;

/// <summary>
/// Host-start validator for <see cref="RabbitMqTransportOptions"/>. Each violation becomes
/// one entry in the resulting <see cref="OptionsValidationException"/>.
/// </summary>
/// <remarks>
/// <para><b>Uri XOR discrete fields:</b> a non-empty <see cref="RabbitMqTransportOptions.Uri"/> is the
/// authoritative connection source; supplying it together with non-default discrete fields
/// (<see cref="RabbitMqTransportOptions.HostName"/> etc.) is rejected as ambiguous.</para>
/// </remarks>
internal sealed class RabbitMqTransportOptionsValidator : IValidateOptions<RabbitMqTransportOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, RabbitMqTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        ValidateConnection(options, failures);
        ValidateTopologyNames(options, failures);
        ValidateBehavior(options, failures);
        ValidateResilience(options, failures);
        ValidateInsecureBroker(options, failures);

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static void ValidateConnection(RabbitMqTransportOptions options, List<string> failures)
    {
        var hasUri = !string.IsNullOrWhiteSpace(options.Uri);
        if (hasUri)
        {
            if (!System.Uri.TryCreate(options.Uri, UriKind.Absolute, out var parsed)
                || (parsed.Scheme != "amqp" && parsed.Scheme != "amqps"))
            {
                failures.Add($"RabbitMqTransportOptions.Uri must be an absolute amqp:// or amqps:// URI (got '{options.Uri}').");
            }

            // Uri XOR discrete fields: any discrete field deviating from its default is ambiguous.
            if (DiscreteFieldsDeviateFromDefault(options))
            {
                failures.Add(
                    "RabbitMqTransportOptions.Uri is set together with non-default discrete connection fields "
                    + "(HostName/Port/VirtualHost/UserName/Password/UseTls). Supply EITHER Uri OR the discrete fields, not both.");
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(options.HostName))
            {
                failures.Add("RabbitMqTransportOptions.HostName is required when Uri is not set (got empty).");
            }
            if (options.Port is < 1 or > 65535)
            {
                failures.Add($"RabbitMqTransportOptions.Port must be in [1, 65535] (got {options.Port}).");
            }
            if (string.IsNullOrWhiteSpace(options.VirtualHost))
            {
                failures.Add("RabbitMqTransportOptions.VirtualHost is required when Uri is not set (got empty).");
            }
            if (string.IsNullOrEmpty(options.UserName))
            {
                failures.Add("RabbitMqTransportOptions.UserName is required when Uri is not set (got empty).");
            }
        }
    }

    private static bool DiscreteFieldsDeviateFromDefault(RabbitMqTransportOptions o) =>
        !string.Equals(o.HostName, "localhost", StringComparison.Ordinal)
        || o.Port != 5672
        || !string.Equals(o.VirtualHost, "/", StringComparison.Ordinal)
        || !string.Equals(o.UserName, "guest", StringComparison.Ordinal)
        || !string.Equals(o.Password, "guest", StringComparison.Ordinal)
        || o.UseTls;

    private static void ValidateTopologyNames(RabbitMqTransportOptions options, List<string> failures)
    {
        RequireNonWhitespace(options.ExchangeName, nameof(options.ExchangeName), failures);
        RequireNonWhitespace(options.DeadLetterExchangeName, nameof(options.DeadLetterExchangeName), failures);
        RequireNonWhitespace(options.DeadLetterQueueName, nameof(options.DeadLetterQueueName), failures);

        // QueuePrefix may legitimately be empty (queue == service name), but not whitespace-only.
        if (options.QueuePrefix is not null && options.QueuePrefix.Length > 0
            && string.IsNullOrWhiteSpace(options.QueuePrefix))
        {
            failures.Add("RabbitMqTransportOptions.QueuePrefix must not be whitespace-only.");
        }
    }

    private static void ValidateBehavior(RabbitMqTransportOptions options, List<string> failures)
    {
        if (options.PrefetchCount == 0)
        {
            failures.Add("RabbitMqTransportOptions.PrefetchCount must be >= 1 (got 0).");
        }
        if (options.MaxRedeliveryCount < 1)
        {
            failures.Add($"RabbitMqTransportOptions.MaxRedeliveryCount must be >= 1 (got {options.MaxRedeliveryCount}).");
        }
        if (options.DefaultRequestTimeout <= TimeSpan.Zero
            || options.DefaultRequestTimeout > TimeSpan.FromMinutes(10))
        {
            failures.Add($"RabbitMqTransportOptions.DefaultRequestTimeout must be in (0, 10 min] (got {options.DefaultRequestTimeout}).");
        }
        if (options.PublisherConfirmTimeout <= TimeSpan.Zero
            || options.PublisherConfirmTimeout > TimeSpan.FromMinutes(5))
        {
            failures.Add($"RabbitMqTransportOptions.PublisherConfirmTimeout must be in (0, 5 min] (got {options.PublisherConfirmTimeout}).");
        }
        if (options.MessageIdCacheCapacity < 16)
        {
            failures.Add($"RabbitMqTransportOptions.MessageIdCacheCapacity must be >= 16 (got {options.MessageIdCacheCapacity}).");
        }
        if (options.ConsumerDispatchConcurrency != 1)
        {
            // v0.1 hard-caps consumer dispatch concurrency to 1. With > 1, multiple
            // OnReceivedAsync handlers run on the SAME non-thread-safe consumer IChannel, and the
            // reply-publish (step 8) + ack (step 9) would race / interleave frames → channel fault.
            // Scale via PrefetchCount + multiple worker instances instead; per-channel concurrency is v0.2.
            failures.Add(
                $"RabbitMqTransportOptions.ConsumerDispatchConcurrency must be 1 in v0.1 (got {options.ConsumerDispatchConcurrency}). "
                + "Parallelism: raise PrefetchCount and/or run multiple worker instances; per-channel concurrency is a v0.2 concern.");
        }
    }

    private static void ValidateResilience(RabbitMqTransportOptions options, List<string> failures)
    {
        if (options.RetryDelays is { Count: > 0 } delays)
        {
            for (var i = 0; i < delays.Count; i++)
            {
                if (delays[i] <= TimeSpan.Zero)
                {
                    failures.Add($"RabbitMqTransportOptions.RetryDelays[{i}] must be positive (got {delays[i]}).");
                }
            }
        }
        if (options.CircuitBreakerThreshold < 1)
        {
            failures.Add($"RabbitMqTransportOptions.CircuitBreakerThreshold must be >= 1 (got {options.CircuitBreakerThreshold}).");
        }
        if (options.CircuitBreakerWindow <= TimeSpan.Zero)
        {
            failures.Add($"RabbitMqTransportOptions.CircuitBreakerWindow must be positive (got {options.CircuitBreakerWindow}).");
        }
    }

    private static void ValidateInsecureBroker(RabbitMqTransportOptions options, List<string> failures)
    {
        if (options.AllowInsecureBroker)
        {
            return; // operator explicitly accepted an internal/trusted-network broker.
        }

        string host;
        bool isGuestDefault;

        if (!string.IsNullOrWhiteSpace(options.Uri))
        {
            if (!System.Uri.TryCreate(options.Uri, UriKind.Absolute, out var parsed))
            {
                return; // a malformed Uri is reported by ValidateConnection; nothing to assess here.
            }
            host = parsed.Host;
            var userInfo = parsed.UserInfo;
            isGuestDefault = userInfo.Length == 0
                || string.Equals(userInfo, "guest", StringComparison.Ordinal)
                || string.Equals(userInfo, "guest:guest", StringComparison.Ordinal);
        }
        else
        {
            host = options.HostName;
            isGuestDefault = string.Equals(options.UserName, "guest", StringComparison.Ordinal)
                && string.Equals(options.Password, "guest", StringComparison.Ordinal);
        }

        // Only the default guest/guest credentials on a NON-loopback broker are rejected: there is no
        // application-level HMAC on this transport, so default credentials reachable off-box are the
        // concrete exposure. Cleartext AMQP without TLS is independently discouraged (see UseTls) but is
        // NOT failed here — a dedicated user on a trusted private network is a legitimate operator choice.
        if (!isGuestDefault || string.IsNullOrWhiteSpace(host) || IsLoopbackHost(host))
        {
            return; // loopback broker (local dev / same host) is exempt, matching the HTTP transport.
        }

        failures.Add(
            $"RabbitMqTransportOptions targets a non-loopback broker host ('{host}') with the default "
            + "guest/guest credentials. Provision a dedicated broker user, or set "
            + "RabbitMqTransportOptions.AllowInsecureBroker=true to accept an internal/trusted-network broker explicitly.");
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return System.Net.IPAddress.TryParse(host, out var ip) && System.Net.IPAddress.IsLoopback(ip);
    }

    private static void RequireNonWhitespace(string? value, string fieldName, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"RabbitMqTransportOptions.{fieldName} is required (got empty/whitespace).");
        }
    }
}
