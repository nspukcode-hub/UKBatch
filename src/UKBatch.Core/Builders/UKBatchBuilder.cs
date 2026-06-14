using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Runtime;
using UKBatch.Abstractions.Storage;
using UKBatch.Abstractions.Transport;
using UKBatch.Discovery;
using UKBatch.Internal;
using UKBatch.Runtime;
using UKBatch.Storage;
using UKBatch.Transport;
using UKBatch.Validation;

namespace UKBatch.Builders;

/// <summary>
/// Root fluent builder for UKBatch registration. Returned by
/// <see cref="ServiceCollectionExtensions.AddUKBatch"/>.
/// </summary>
public sealed class UKBatchBuilder
{
    private readonly List<JobBuilder> _jobBuilders = new();
    private readonly List<Action<UKBatchOptions>> _optionsConfigurations = new();
    private readonly List<(string Name, Action<BatchBuilder> Configure)> _batchBuilders = new();
    private readonly List<Assembly> _additionalAssemblies = new();
    private readonly Registry.JobDefinitionRegistry _jobRegistry = new();
    private readonly Registry.BatchDefinitionRegistry _batchRegistry = new();

    /// <summary>Underlying service collection — adapter packages add their stores/transports here.</summary>
    public IServiceCollection Services { get; }

    internal UKBatchBuilder(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Services = services;
        // Core singletons used everywhere downstream — registered once here.
        Services.AddOptions<UKBatchOptions>();
        Services.AddSingleton<IValidateOptions<UKBatchOptions>, UKBatchOptionsValidator>();
        Services.TryAddSingleton(TimeProvider.System);
        Services.TryAddSingleton(_jobRegistry);
        Services.TryAddSingleton(_batchRegistry);
        // Expose the registry through the IBatchDefinitionLookup contract.
        // Factory-resolution guarantees same-instance with the concrete singleton above.
        Services.TryAddSingleton<IBatchDefinitionLookup>(sp => sp.GetRequiredService<Registry.BatchDefinitionRegistry>());
        // Expose the job registry through the IJobDefinitionLookup contract.
        Services.TryAddSingleton<IJobDefinitionLookup>(sp => sp.GetRequiredService<Registry.JobDefinitionRegistry>());
    }

    /// <summary>
    /// Mutates the <see cref="UKBatchOptions"/> singleton. Equivalent to
    /// <c>services.Configure&lt;UKBatchOptions&gt;(configure)</c> but chainable.
    /// </summary>
    public UKBatchBuilder Configure(Action<UKBatchOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _optionsConfigurations.Add(configure);
        Services.Configure(configure);
        return this;
    }

    /// <summary>
    /// Registers <see cref="InMemoryJobStore"/>, <see cref="InMemoryBatchDefinitionStore"/>, and
    /// <see cref="InMemoryApprovalGateStore"/> as singletons. Also exposes the job store
    /// through <see cref="IJobStoreInternal"/> (the runtime <c>InsertAsync</c> seam) and registers the
    /// durable-approval-record default so InProcess works without the EF adapter.
    /// </summary>
    public UKBatchBuilder UseInMemoryStorage()
    {
        Services.AddSingleton<InMemoryJobStore>();
        Services.AddSingleton<IJobStore>(sp => sp.GetRequiredService<InMemoryJobStore>());
        Services.AddSingleton<IJobStoreInternal>(sp => sp.GetRequiredService<InMemoryJobStore>());
        Services.AddSingleton<IJobExecutionReader>(sp => sp.GetRequiredService<InMemoryJobStore>());
        Services.AddSingleton<IJobExecutionWriter>(sp => sp.GetRequiredService<InMemoryJobStore>());
        Services.AddSingleton<IBatchDefinitionStore, InMemoryBatchDefinitionStore>();
        Services.AddSingleton<IApprovalGateStore, InMemoryApprovalGateStore>();
        return this;
    }

    /// <summary>Registers <see cref="InProcessTransport"/> as the singleton <see cref="ITransport"/>.</summary>
    public UKBatchBuilder UseInProcessTransport()
    {
        Services.AddSingleton<InProcessTransport>();
        Services.AddSingleton<ITransport>(sp => sp.GetRequiredService<InProcessTransport>());
        return this;
    }

    /// <summary>Registers an <see cref="IJob"/> implementation, returning a per-job options builder.</summary>
    /// <remarks>
    /// The <see cref="UKBatchOptions"/> snapshot is NOT captured here. It is built once in
    /// <see cref="Complete"/> after every <see cref="Configure"/> call has run, then passed to each
    /// <see cref="JobBuilder.Apply"/>. Order-independent registration.
    /// </remarks>
    public JobBuilder AddJob<TJob>()
        where TJob : class, IJob
    {
        var builder = new JobBuilder(Services, typeof(TJob), isPartitioned: false, registry: _jobRegistry);
        _jobBuilders.Add(builder);
        return builder;
    }

    /// <summary>Registers an <see cref="IPartitionedJob{TItem}"/> implementation, returning a per-job options builder.</summary>
    /// <remarks>
    /// The <see cref="UKBatchOptions"/> snapshot is NOT captured here. It is built once in
    /// <see cref="Complete"/> after every <see cref="Configure"/> call has run, then passed to each
    /// <see cref="JobBuilder.Apply"/>. Order-independent registration.
    /// </remarks>
    public JobBuilder AddPartitionedJob<TJob, TItem>()
        where TJob : class, IPartitionedJob<TItem>
    {
        var builder = new JobBuilder(Services, typeof(TJob), isPartitioned: true, registry: _jobRegistry);
        _jobBuilders.Add(builder);
        return builder;
    }

    /// <summary>
    /// Registers a code-defined batch. <paramref name="name"/> is the display name;
    /// <paramref name="configure"/> assembles the steps.
    /// </summary>
    public UKBatchBuilder AddBatch(string name, Action<BatchBuilder> configure)
    {
        // Whitespace-only names are a programmer error caught here at the registration boundary.
        // Whitespace is permitted at the lookup boundary (see IBatchDefinitionLookup.TryGetByName
        // xmldoc for the asymmetry rationale).
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        _batchBuilders.Add((name, configure));
        return this;
    }

    /// <summary>
    /// Bulk-scans the given assemblies for <c>[Job]</c>-decorated types. Routes through
    /// <see cref="Configure"/> so the resulting <see cref="UKBatchOptions.AdditionalAssembliesToScan"/>
    /// is observed by <see cref="AttributeJobDiscovery"/>. No-op after registration completes.
    /// </summary>
    public UKBatchBuilder ScanAssemblies(params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        foreach (var asm in assemblies)
        {
            if (asm is not null && !_additionalAssemblies.Contains(asm))
            {
                _additionalAssemblies.Add(asm);
            }
        }
        return this;
    }

    /// <summary>
    /// Materialises every registered job into the registry + DI, registers every code-defined batch,
    /// runs attribute discovery, and wires the runtime singletons + hosted services. Called by
    /// <see cref="ServiceCollectionExtensions.AddUKBatch"/> after the user's configuration callback.
    /// </summary>
    internal void Complete()
    {
        var optionsSnapshot = BuildOptionsSnapshot();

        // Propagate the assembly list to the runtime options too (so anyone querying IOptions sees them).
        if (_additionalAssemblies.Count > 0)
        {
            var assemblies = _additionalAssemblies.ToArray();
            Services.PostConfigure<UKBatchOptions>(opts =>
            {
                // Mutate via reflection because the property is `init`-only on the public surface
                // but conceptually populated by the builder before any consumer reads it.
                opts.GetType()
                    .GetProperty(nameof(UKBatchOptions.AdditionalAssembliesToScan))!
                    .SetValue(opts, assemblies);
            });
        }

        // Build a discovery-only snapshot that DOES include the builder's assembly list.
        var discoverySnapshot = new UKBatchOptions
        {
            AdditionalAssembliesToScan = _additionalAssemblies.ToArray(),
        };
        foreach (var cfg in _optionsConfigurations)
        {
            cfg(discoverySnapshot);
        }

        // Materialise explicitly-registered jobs first so attribute discovery can skip them.
        // Pass the FINAL snapshot at apply time so any Configure(...) call sequenced AFTER
        // AddJob<T>() still takes effect on the resulting JobDefinition.
        foreach (var jb in _jobBuilders)
        {
            jb.Apply(optionsSnapshot);
        }
        // Then attribute discovery for un-registered job types.
        AttributeJobDiscovery.DiscoverAndRegister(Services, _jobRegistry, discoverySnapshot);

        // Materialise code-defined batches.
        var now = DateTimeOffset.UtcNow;
        foreach (var (name, configure) in _batchBuilders)
        {
            var bb = new BatchBuilder(optionsSnapshot);
            configure(bb);
            var def = bb.Build(IdGenerator.NewBatchId(), name, now);
            var validation = BatchDefinitionValidator.Validate(def);
            if (!validation.IsValid)
            {
                var errors = string.Join("; ", validation.Errors.Select(e => $"{e.PropertyPath}: {e.Message}"));
                throw new InvalidOperationException($"Batch '{name}' configuration invalid: {errors}");
            }
            _batchRegistry.Register(def);
        }

        // Runtime singletons.
        // The shared in-process watch fan-out hub. Registered unconditionally so EVERY store
        // (InMemory or the EF adapter) composes the SAME singleton via the Abstractions-public
        // IJobExecutionWatchHub — no friend access, no per-adapter re-register.
        Services.TryAddSingleton<JobExecutionWatchHub>();
        Services.TryAddSingleton<IJobExecutionWatchHub>(sp => sp.GetRequiredService<JobExecutionWatchHub>());
        Services.TryAddSingleton<CronExpressionCache>();
        Services.TryAddSingleton<JobDispatcher>();
        Services.TryAddSingleton<JobScheduler>();
        Services.TryAddSingleton<DebouncedProgressFlusher>();
        Services.TryAddSingleton<JobExecutionAwaiter>();
        Services.TryAddSingleton<IJobExecutionAwaiter>(sp => sp.GetRequiredService<JobExecutionAwaiter>());
        Services.TryAddSingleton<ApprovalGateService>();
        Services.TryAddSingleton<IApprovalGateService>(sp => sp.GetRequiredService<ApprovalGateService>());
        Services.TryAddSingleton<IApprovalGateCoordinator>(sp => sp.GetRequiredService<ApprovalGateService>());
        // Friend seam for the SignalR hub fan-out pump.
        Services.TryAddSingleton<IApprovalGateEvents>(sp => sp.GetRequiredService<ApprovalGateService>());
        // Friend seam for the SignalR hub progress fan-out.
        Services.TryAddSingleton<IProgressBeatBroadcaster>(sp => sp.GetRequiredService<DebouncedProgressFlusher>());
        // Composite batch catalog (Code + Store).
        Services.TryAddSingleton<IBatchCatalogService, BatchCatalogService>();
        // Dispatcher backpressure probe (consumed by UKBatchHealthCheck).
        Services.TryAddSingleton<JobDispatcherProbe>();
        Services.TryAddSingleton<IDispatcherProbe>(sp => sp.GetRequiredService<JobDispatcherProbe>());
        // Runtime-driven batch completion signal for the hub fan-out.
        // JobRunner.TriggerBatchAsync writes the batch RUN id to this channel after the
        // BatchExecutor finishes. JobStatusHubFanout subscribes and queries the store ONCE per
        // signal to build the aggregate summary. Replaces the per-watch-event tracker pattern.
        Services.TryAddSingleton<BatchCompletionSignal>();
        Services.TryAddSingleton<IBatchCompletionEvents>(sp => sp.GetRequiredService<BatchCompletionSignal>());
        Services.TryAddSingleton<JobRunner>();
        Services.TryAddSingleton<IJobRunner>(sp => sp.GetRequiredService<JobRunner>());
        Services.TryAddSingleton<IJobRunnerInternal>(sp => sp.GetRequiredService<JobRunner>());
        Services.TryAddSingleton<IRetryPolicy>(
            _ => new ExponentialRetryPolicy(TimeSpan.FromSeconds(1), 2.0, TimeSpan.FromMinutes(1)));
        Services.TryAddSingleton<JobWorker>();
        Services.AddSingleton<IHostedService, UKBatchHost>();
    }

    /// <summary>
    /// Builds an in-memory snapshot of the current options (used by builders that need defaults
    /// during registration). Does NOT include <see cref="UKBatchOptions.AdditionalAssembliesToScan"/>
    /// — that list is tracked separately on the builder and passed to discovery directly.
    /// </summary>
    private UKBatchOptions BuildOptionsSnapshot()
    {
        var snap = new UKBatchOptions();
        foreach (var cfg in _optionsConfigurations)
        {
            cfg(snap);
        }
        return snap;
    }
}
