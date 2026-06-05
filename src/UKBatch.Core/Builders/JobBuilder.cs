using Microsoft.Extensions.DependencyInjection;
using Polly;
using UKBatch.Abstractions.Jobs;
using UKBatch.Discovery;

namespace UKBatch.Builders;

/// <summary>
/// Per-job fluent options. Each method overrides the value (if any) supplied via
/// <see cref="JobAttribute"/>.
/// </summary>
/// <remarks>
/// The <see cref="UKBatchOptions"/> snapshot is NOT captured at construction time. Cron-format and
/// default-value resolution are deferred to <see cref="Apply"/>, which is invoked by
/// <see cref="UKBatchBuilder.Complete"/> after ALL <c>Configure(...)</c> calls have run. This means
/// <c>builder.AddJob&lt;T&gt;(); builder.Configure(opts =&gt; opts.DefaultMaxRetries = 5);</c>
/// applies the configure intent correctly — order-independent registration.
/// </remarks>
public sealed class JobBuilder
{
    private readonly IServiceCollection _services;
    private readonly Type _implementationType;
    private readonly Type? _partitionItemType;
    private readonly bool _isPartitioned;
    private readonly Registry.JobDefinitionRegistry _registry;

    private string? _name;
    private string? _schedule;
    private int? _maxRetries;
    private int? _timeoutSeconds;
    private int? _partitionWorkerCount;
    private ItemErrorPolicy _itemErrorPolicy;
    private string[]? _tags;
    private IReadOnlyDictionary<string, object?>? _defaultParameters;

    internal JobBuilder(
        IServiceCollection services,
        Type implementationType,
        Type? partitionItemType,
        bool isPartitioned,
        Registry.JobDefinitionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(implementationType);
        ArgumentNullException.ThrowIfNull(registry);
        _services = services;
        _implementationType = implementationType;
        _partitionItemType = partitionItemType;
        _isPartitioned = isPartitioned;
        _registry = registry;
        _itemErrorPolicy = ItemErrorPolicy.FailFast;

        // Seed defaults from the [Job] attribute if present.
        var attr = (JobAttribute?)Attribute.GetCustomAttribute(implementationType, typeof(JobAttribute), inherit: false);
        if (attr is not null)
        {
            _name = attr.Name;
            _schedule = attr.Schedule;
            _maxRetries = attr.MaxRetries;
            _timeoutSeconds = attr.TimeoutSeconds;
            _tags = attr.Tags;
        }
    }

    /// <summary>Overrides the job name.</summary>
    public JobBuilder Named(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _name = name;
        return this;
    }

    /// <summary>
    /// Sets a cron schedule. The expression is stored verbatim and validated against the FINAL
    /// <see cref="UKBatchOptions.CronFormat"/> inside <see cref="Apply"/>, so configure calls
    /// occurring AFTER this builder still take effect. Throws <see cref="ArgumentException"/>
    /// at registration completion (<see cref="UKBatchBuilder.Complete"/>) on parse failure.
    /// </summary>
    public JobBuilder WithSchedule(string cronExpression)
    {
        ArgumentException.ThrowIfNullOrEmpty(cronExpression);
        _schedule = cronExpression;
        return this;
    }

    /// <summary>Sets max retries; must be &gt;= 0.</summary>
    public JobBuilder WithMaxRetries(int maxRetries)
    {
        if (maxRetries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRetries), maxRetries, "must be >= 0");
        }
        _maxRetries = maxRetries;
        return this;
    }

    /// <summary>Sets timeout in seconds; 0 = no timeout. Must be &gt;= 0.</summary>
    public JobBuilder WithTimeout(int timeoutSeconds)
    {
        if (timeoutSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), timeoutSeconds, "must be >= 0");
        }
        _timeoutSeconds = timeoutSeconds;
        return this;
    }

    /// <summary>Partitioned-job only: worker count. Must be &gt;= 1.</summary>
    public JobBuilder WithParallelism(int workerCount)
    {
        if (workerCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(workerCount), workerCount, "must be >= 1");
        }
        _partitionWorkerCount = workerCount;
        return this;
    }

    /// <summary>Partitioned-job only: per-item error policy.</summary>
    public JobBuilder WithItemErrorPolicy(ItemErrorPolicy policy)
    {
        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy), policy, "unknown ItemErrorPolicy");
        }
        _itemErrorPolicy = policy;
        return this;
    }

    /// <summary>Routing tags (worker mode). Replaces any tags from the <c>[Job]</c> attribute.</summary>
    public JobBuilder WithTags(params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        _tags = tags;
        return this;
    }

    /// <summary>
    /// Static default parameters merged into every dispatch unless overridden at trigger time.
    /// Defensive-copied by <c>JobDefinitionFactory</c> at registration (a trusted callsite for
    /// later <c>WrapWithoutCopy</c> use).
    /// </summary>
    public JobBuilder WithDefaultParameters(IReadOnlyDictionary<string, object?> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        _defaultParameters = parameters;
        return this;
    }

    /// <summary>
    /// Materialises the registration into the registry + DI container using the FINAL options
    /// snapshot produced by <see cref="UKBatchBuilder.Complete"/>. Cron expressions are validated
    /// here (deferred from <see cref="WithSchedule"/>) against
    /// <paramref name="finalOptions"/>.<see cref="UKBatchOptions.CronFormat"/>, so the validation
    /// honours every <c>Configure(...)</c> call regardless of registration order.
    /// </summary>
    internal void Apply(UKBatchOptions finalOptions)
    {
        ArgumentNullException.ThrowIfNull(finalOptions);
        var jobName = _name ?? _implementationType.FullName ?? _implementationType.Name;

        // Deferred cron validation: use the FINAL snapshot's CronFormat so a Configure(...) call
        // after WithSchedule(...) still takes effect.
        if (!string.IsNullOrEmpty(_schedule))
        {
            try
            {
                _ = Cronos.CronExpression.Parse(_schedule, finalOptions.CronFormat);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Invalid cron expression '{_schedule}' for job '{jobName}' against CronFormat={finalOptions.CronFormat}: {ex.Message}",
                    ex);
            }
        }

        var def = JobDefinitionFactory.Build(
            name: jobName,
            implementationType: _implementationType,
            isPartitioned: _isPartitioned,
            schedule: _schedule,
            maxRetries: _maxRetries ?? finalOptions.DefaultMaxRetries,
            timeoutSeconds: _timeoutSeconds ?? finalOptions.DefaultTimeoutSeconds,
            partitionWorkerCount: _isPartitioned ? (_partitionWorkerCount ?? finalOptions.DefaultPartitionWorkerCount) : 0,
            itemErrorPolicy: _itemErrorPolicy,
            defaultParameters: _defaultParameters,
            tags: _tags);
        var validation = Validation.JobDefinitionValidator.Validate(def);
        if (!validation.IsValid)
        {
            var errors = string.Join("; ", validation.Errors.Select(e => $"{e.PropertyPath}: {e.Message}"));
            throw new InvalidOperationException($"Job '{jobName}' configuration invalid: {errors}");
        }
        ResiliencePipeline? pipeline = def.ItemErrorPolicy == ItemErrorPolicy.RetryThenContinue && def.MaxRetries >= 1
            ? JobDefinitionFactory.BuildItemRetryPipeline(def)
            : null;
        _registry.Register(def, _implementationType, pipeline);

        // Scoped DI registration (the implementation type itself; JobWorker resolves
        // via GetRequiredService(implType) directly — no IJob multi-binding ambiguity).
        _services.AddScoped(_implementationType);
        _ = _partitionItemType;
    }
}
