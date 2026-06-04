using UKBatch.Abstractions.Batches;

namespace UKBatch.Runtime;

/// <summary>
/// Internal Core seam exposing the runtime-side "wait for approval" entry point.
/// <para>
/// The public <see cref="Abstractions.Storage.IApprovalGateService"/> (dashboard / API) does NOT
/// expose <c>AwaitApprovalAsync</c>; only the <c>BatchExecutor</c> sees this seam. Both interfaces
/// resolve to the same singleton (see <c>ServiceCollectionExtensions.AddUKBatch</c>).
/// </para>
/// </summary>
internal interface IApprovalGateCoordinator
{
    /// <summary>
    /// Blocks until the approval gate is resolved (approved, rejected, timed out, or cancelled).
    /// Throws <c>BatchStepFailureException</c> on Rejected / TimedOutFail; <see cref="OperationCanceledException"/>
    /// on cancellation.
    /// </summary>
    /// <param name="batchId">Parent batch RUN id.</param>
    /// <param name="stepId">Parent batch step id.</param>
    /// <param name="config">Approval gate configuration.</param>
    /// <param name="batchName">
    /// Definition display name of the parent batch — carried verbatim onto the pending-approval
    /// snapshot the dashboard renders. (The caller has the <c>BatchDefinition</c> in scope;
    /// <paramref name="batchId"/> is a RUN id and cannot resolve the name via the definition lookup.)
    /// </param>
    /// <param name="batchDefinitionId">Definition id of the parent batch — persisted on the durable record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AwaitApprovalAsync(
        string batchId,
        string stepId,
        ApprovalGateConfig config,
        string batchName,
        string batchDefinitionId,
        CancellationToken cancellationToken);
}
