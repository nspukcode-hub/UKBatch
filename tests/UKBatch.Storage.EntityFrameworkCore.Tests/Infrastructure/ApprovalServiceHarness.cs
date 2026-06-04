using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Storage;

namespace UKBatch.Storage.EntityFrameworkCore.Tests.Infrastructure;

/// <summary>
/// Drives the real (Core-internal) <c>ApprovalGateService</c> over a chosen <see cref="IApprovalGateStore"/>
/// so the EF test project can exercise the durable write-through + restart-merge end-to-end. The service
/// type + its internal <c>IApprovalGateCoordinator.AwaitApprovalAsync</c> seam are reached by reflection
/// (both internal to Core; the EF test project has no friend grant to Core — by design).
/// </summary>
/// <remarks>
/// A real <c>AddUKBatch</c> container is built, then the <see cref="IApprovalGateStore"/> registration is
/// overridden with the supplied store (last-registration-wins for a single resolve). Resolving the public
/// <see cref="IApprovalGateService"/> + the internal coordinator yields the SAME singleton — so a gate
/// created via the coordinator is decidable via the public service, and its outcome is written through to
/// the supplied store (the path).
/// </remarks>
internal sealed class ApprovalServiceHarness : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly object _coordinator;        // IApprovalGateCoordinator (internal)
    private readonly MethodInfo _awaitApproval;

    public ApprovalServiceHarness(IApprovalGateStore store, TimeProvider clock)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(clock);
        services.AddUKBatch(_ => { });
        // Override the default InMemoryApprovalGateStore with the supplied store (last wins).
        services.AddSingleton(store);
        _provider = services.BuildServiceProvider();

        Service = _provider.GetRequiredService<IApprovalGateService>();

        // Resolve the internal IApprovalGateCoordinator by its Type (DI resolution ignores accessibility).
        // Anchor on a PUBLIC Core type to reach the Core assembly without a friend grant.
        var coreAssembly = typeof(UKBatch.Storage.InMemoryApprovalGateStore).Assembly;
        var coordinatorType = coreAssembly
            .GetType("UKBatch.Runtime.IApprovalGateCoordinator", throwOnError: true)!;
        _coordinator = _provider.GetRequiredService(coordinatorType);
        _awaitApproval = coordinatorType.GetMethod("AwaitApprovalAsync")!;
    }

    /// <summary>The public service (ListPending / Approve / Reject) — same singleton as the coordinator.</summary>
    public IApprovalGateService Service { get; }

    /// <summary>Starts an approval gate (the BatchExecutor seam). Returns the awaiting task (resolves on decision/cancel).</summary>
    /// <remarks>
    /// The coordinator seam gained <c>batchName</c> + <c>batchDefinitionId</c> params (FIX 3
    /// the dashboard "&lt;unknown&gt;" batch fix; <c>batchId</c> is a RUN id, the name/def-id are threaded
    /// from the executor's <c>BatchDefinition</c>). These EF durability tests assert on <c>BatchId</c> +
    /// <c>ApprovalId</c> only, so fixed placeholder values suffice here.
    /// </remarks>
    public Task AwaitApprovalAsync(string batchId, string stepId, ApprovalGateConfig config, CancellationToken ct)
        => (Task)_awaitApproval.Invoke(_coordinator, new object[] { batchId, stepId, config, "batch-name", "batch-def-1", ct })!;

    public void Dispose() => _provider.Dispose();
}
