namespace UKBatch.Builders;

/// <summary>Per-job-step options inside a batch (overrides for the job's defaults).</summary>
public sealed class JobStepBuilder
{
    internal int? MaxRetries { get; private set; }
    internal int? TimeoutSeconds { get; private set; }
    internal IReadOnlyDictionary<string, object?>? Parameters { get; private set; }
    internal string? TargetService { get; private set; }

    /// <summary>Overrides the job's max retries for this step.</summary>
    public JobStepBuilder WithMaxRetries(int maxRetries)
    {
        if (maxRetries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRetries), maxRetries, "must be >= 0");
        }
        MaxRetries = maxRetries;
        return this;
    }

    /// <summary>Overrides the job's timeout for this step (in seconds; 0 = no timeout).</summary>
    public JobStepBuilder WithTimeout(int timeoutSeconds)
    {
        if (timeoutSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), timeoutSeconds, "must be >= 0");
        }
        TimeoutSeconds = timeoutSeconds;
        return this;
    }

    /// <summary>Sets static parameters for this step (defensive-copied).</summary>
    public JobStepBuilder WithParameters(IReadOnlyDictionary<string, object?> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        Parameters = new Dictionary<string, object?>(parameters, StringComparer.Ordinal);
        return this;
    }

    /// <summary>Specifies the target service for cross-service jobs (worker mode).</summary>
    public JobStepBuilder OnService(string targetService)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetService);
        TargetService = targetService;
        return this;
    }
}
