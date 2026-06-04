using Microsoft.Extensions.Options;

namespace UKBatch;

/// <summary>
/// Validates <see cref="UKBatchOptions"/> at host start. Failure throws
/// <see cref="OptionsValidationException"/> so misconfiguration fails fast.
/// </summary>
internal sealed class UKBatchOptionsValidator : IValidateOptions<UKBatchOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, UKBatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.MaxDegreeOfParallelism < 1)
        {
            failures.Add($"MaxDegreeOfParallelism must be >= 1 (was {options.MaxDegreeOfParallelism}).");
        }

        if (options.DispatcherChannelCapacity != 0 && options.DispatcherChannelCapacity < options.MaxDegreeOfParallelism)
        {
            failures.Add($"DispatcherChannelCapacity must be >= MaxDegreeOfParallelism (was {options.DispatcherChannelCapacity}, MaxDoP={options.MaxDegreeOfParallelism}).");
        }

        if (options.ShutdownTimeout < TimeSpan.Zero)
        {
            failures.Add("ShutdownTimeout must be >= TimeSpan.Zero.");
        }

        if (options.ProgressFlushInterval <= TimeSpan.Zero)
        {
            failures.Add("ProgressFlushInterval must be > TimeSpan.Zero.");
        }

        if (options.DefaultMaxRetries < 0)
        {
            failures.Add("DefaultMaxRetries must be >= 0.");
        }

        if (options.DefaultTimeoutSeconds < 0)
        {
            failures.Add("DefaultTimeoutSeconds must be >= 0.");
        }

        if (options.DefaultPartitionWorkerCount < 1)
        {
            failures.Add("DefaultPartitionWorkerCount must be >= 1.");
        }

        if (options.WatchBufferCapacity < 1)
        {
            failures.Add("WatchBufferCapacity must be >= 1.");
        }

        if (options.HubBufferCapacity < 1)
        {
            failures.Add("HubBufferCapacity must be >= 1.");
        }

        if (options.MaxPageLimit < 1)
        {
            failures.Add("MaxPageLimit must be >= 1.");
        }

        if (options.DefaultPageLimit < 1)
        {
            failures.Add("DefaultPageLimit must be >= 1.");
        }

        if (options.DefaultPageLimit > options.MaxPageLimit)
        {
            failures.Add($"DefaultPageLimit ({options.DefaultPageLimit}) must be <= MaxPageLimit ({options.MaxPageLimit}).");
        }

        if (string.IsNullOrWhiteSpace(options.HubPath))
        {
            failures.Add("HubPath must be non-empty / non-whitespace.");
        }
        else if (!options.HubPath.StartsWith('/'))
        {
            failures.Add("HubPath must start with '/'.");
        }

        if (options.MaxQueryStatusesCount < 1)
        {
            failures.Add("MaxQueryStatusesCount must be >= 1.");
        }

        if (options.MaxQuerySearchTextLength < 1)
        {
            failures.Add("MaxQuerySearchTextLength must be >= 1.");
        }

        // Validate ApprovalRoleClaimTypes.
        if (options.ApprovalRoleClaimTypes is null || options.ApprovalRoleClaimTypes.Count == 0)
        {
            failures.Add($"{nameof(UKBatchOptions.ApprovalRoleClaimTypes)} must contain at least 1 claim type.");
        }
        else
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in options.ApprovalRoleClaimTypes)
            {
                if (string.IsNullOrWhiteSpace(t))
                {
                    failures.Add($"{nameof(UKBatchOptions.ApprovalRoleClaimTypes)} contains a null or whitespace entry.");
                    break;
                }
                if (!seen.Add(t))
                {
                    failures.Add($"{nameof(UKBatchOptions.ApprovalRoleClaimTypes)} contains a duplicate entry '{t}'.");
                    break;
                }
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
