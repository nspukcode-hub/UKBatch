using UKBatch.Abstractions.Models;

namespace UKBatch.Abstractions.Runtime;

/// <summary>
/// Terminal-state awaiter for job executions. Implemented by the runtime (<c>UKBatch.Core</c>);
/// consumers obtain via DI. Consumed by REST sync trigger endpoints and the HTTP transport
/// <c>/invoke</c> receiver.
/// </summary>
/// <remarks>
/// <para><b>Lifecycle invariant:</b> the 4-step awaiter-before-trigger pattern
/// (pre-allocate execId → <see cref="WaitForTerminalAsync"/> → trigger → await) MUST be paired with
/// <see cref="CancelWaiter"/> in a try/catch around the trigger call. If <c>TriggerInternalAsync</c>
/// throws a non-OCE exception (e.g. unregistered job), control never reaches the awaited task; the
/// waiter entry would otherwise leak (one TCS + one CancellationTokenRegistration per failed trigger)
/// because the watch-loop completion path never fires for an execution that was never enqueued.</para>
/// <para><b>Thread-safety:</b> implementations MUST be thread-safe across all members. Multiple
/// concurrent callers may pre-allocate distinct execution ids and await terminal status without
/// external synchronization. The runtime impl multiplexes a single
/// <see cref="UKBatch.Abstractions.Storage.IJobExecutionReader.WatchAsync"/> subscription across
/// all waiters.</para>
/// </remarks>
public interface IJobExecutionAwaiter
{
    /// <summary>
    /// Awaits the terminal status of the given execution.
    /// </summary>
    /// <remarks>
    /// <para><b>Ordering invariant:</b> the implementation MUST register the
    /// waiter into its internal dict SYNCHRONOUSLY before returning the Task — callers rely on the
    /// registration being observable before they trigger the work that this Task will eventually
    /// complete.</para>
    /// </remarks>
    Task<JobExecution> WaitForTerminalAsync(string executionId, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a waiter for an execution whose trigger failed before reaching the dispatcher.
    /// Cancels the underlying TCS so any awaiter unblocks with <see cref="TaskCanceledException"/>,
    /// and the <c>ContinueWith</c> cleanup disposes the cancellation registration. Idempotent —
    /// safe to call when no waiter exists (e.g. trigger succeeded then the caller bailed).
    /// </summary>
    /// <remarks>
    /// Callers MUST invoke this from a catch wrapping <c>TriggerInternalAsync</c> in the 4-step
    /// awaiter-before-trigger pattern (see type-level remarks). Without it, every failed trigger
    /// leaks the TCS/registration pair until process exit.
    /// </remarks>
    void CancelWaiter(string executionId);
}
