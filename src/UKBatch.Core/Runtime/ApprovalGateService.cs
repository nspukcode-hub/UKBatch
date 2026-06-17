using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Internal;

namespace UKBatch.Runtime;

/// <summary>
/// Implements <see cref="IApprovalGateService"/> (public, frozen contract),
/// <see cref="IApprovalGateCoordinator"/> (internal Core seam), and
/// <see cref="IApprovalGateEvents"/> (friend seam for the SignalR hub fan-out).
/// </summary>
/// <remarks>
/// <para>Per-gate TaskCompletionSource + per-gate Task.Delay — sub-100ms timeout drift on
/// the .NET TimerQueue.</para>
/// <para>A negative remaining-time is treated as "fire OnTimeout immediately"
/// without entering Task.Delay (Task.Delay with a negative span would throw).</para>
/// </remarks>
internal sealed class ApprovalGateService : IApprovalGateService, IApprovalGateCoordinator, IApprovalGateEvents
{
    private const string CancelledSentinel = "<cancelled>";

    private readonly ConcurrentDictionary<string, ApprovalGateRegistration> _gates = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly IBatchDefinitionLookup _batchLookup;
    private readonly IApprovalGateStore _approvalStore;
    private readonly ILogger<ApprovalGateService> _logger;

    // Bounded channel of new-gate registrations; consumed by the SignalR hub fan-out.
    // Capacity is intentionally generous (1024) so the hub pump rarely contends with the gate
    // registration thread. Overflow drops the OLDEST event — consistent with the in-memory
    // WatchOverflowPolicy.Backpressure posture.
    private readonly Channel<PendingApproval> _newGates = Channel.CreateBounded<PendingApproval>(
        new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>Constructs the service.</summary>
    /// <remarks>
    /// <paramref name="approvalStore"/> is the durable-record write-through (default
    /// <c>InMemoryApprovalGateStore</c> in Core DI so InProcess is unaffected; the EF adapter replaces
    /// it). Every terminal outcome — approve/reject/auto-approve/timeout-fail/cancel — writes a record
    /// through it exactly once from the centralized resolution path.
    /// </remarks>
    public ApprovalGateService(
        TimeProvider clock,
        IBatchDefinitionLookup batchLookup,
        IApprovalGateStore approvalStore,
        ILogger<ApprovalGateService> logger)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(batchLookup);
        ArgumentNullException.ThrowIfNull(approvalStore);
        ArgumentNullException.ThrowIfNull(logger);
        _clock = clock;
        _batchLookup = batchLookup;
        _approvalStore = approvalStore;
        _logger = logger;
    }

    // ===== IApprovalGateEvents =====

    /// <inheritdoc/>
    public ChannelReader<PendingApproval> NewGates => _newGates.Reader;

    // ===== IApprovalGateCoordinator (internal Core seam) =====

    /// <inheritdoc/>
    public async Task AwaitApprovalAsync(string batchId, string stepId, ApprovalGateConfig config, string batchName, string batchDefinitionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchId);
        ArgumentException.ThrowIfNullOrEmpty(stepId);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrEmpty(batchName);
        ArgumentException.ThrowIfNullOrEmpty(batchDefinitionId);

        var approvalId = IdGenerator.NewApprovalId();
        var nowUtc = _clock.GetUtcNow();
        var deadline = config.TimeoutAfter is { } t ? nowUtc + t : (DateTimeOffset?)null;
        using var gateCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tcs = new TaskCompletionSource<ApprovalOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        var registration = new ApprovalGateRegistration
        {
            ApprovalId = approvalId,
            BatchId = batchId,
            StepId = stepId,
            BatchName = batchName,
            BatchDefinitionId = batchDefinitionId,
            Config = config,
            PendingSinceUtc = nowUtc,
            DeadlineUtc = deadline,
            Tcs = tcs,
            GateCts = gateCts,
        };
        _gates[approvalId] = registration;

        // Durable RECORD write-through BEFORE we announce the gate to the dashboard.
        // Ordering is dict → store → channel:
        //   - dict (above) is first because the awaiter's tcs must be registered before any approve
        //     call can resolve it;
        //   - the durable Pending record (here) must exist before the SignalR fan-out (below), so a
        //     dashboard click can never race a not-yet-persisted gate;
        //   - awaiting this SaveAsync HERE (before `await tcs.Task` at the resolution below) gives the
        //     crash-window happens-before guarantee: the create-write strictly precedes any
        //     outcome-write in-process (a tcs cannot resolve until this method passes this point).
        // Cold path (once per gate, the batch is about to block on a human) — the round-trip is irrelevant.
        try
        {
            await _approvalStore.SaveAsync(
                new PersistedApprovalGate
                {
                    ApprovalId = approvalId,
                    BatchId = batchId,
                    BatchStepId = stepId,
                    BatchDefinitionId = batchDefinitionId,
                    Config = config,
                    Status = ApprovalRecordStatus.Pending,
                    PendingSinceUtc = nowUtc,
                    DeadlineUtc = deadline,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The create-time persist failed (store unreachable) or was cancelled. The registration was put
            // in _gates ABOVE so an approve call could resolve its tcs; but we have NOT yet reached
            // AwaitGateOutcomeAsync, whose finally is what removes it. Without this the registration would
            // leak as an un-removable, un-decidable ghost pending gate (and, under a flaky store, grow the
            // dict unbounded). Roll the dict insert back before the exception propagates to the batch.
            _gates.TryRemove(approvalId, out _);
            throw;
        }

        // Emit to the hub fan-out channel as a PendingApproval snapshot, then register + await the gate's
        // lifecycle. BatchName is the definition display name threaded from the BatchExecutor (which holds
        // the BatchDefinition). It must NOT be resolved via `_batchLookup.TryGetById(batchId)`, because
        // `batchId` is the batch RUN id while the lookup keys on the DEFINITION id — that would always miss
        // and render "<unknown>".
        var announcement = new PendingApproval
        {
            ApprovalId = approvalId,
            BatchId = batchId,
            BatchStepId = stepId,
            BatchName = batchName,
            Config = config,
            PendingSinceUtc = nowUtc,
            DeadlineUtc = deadline,
        };
        await AwaitGateOutcomeAsync(registration, gateCts, announcement, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task ReattachApprovalAsync(
        string approvalId,
        string batchId,
        string stepId,
        ApprovalGateConfig config,
        string batchName,
        string batchDefinitionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(approvalId);
        ArgumentException.ThrowIfNullOrEmpty(batchId);
        ArgumentException.ThrowIfNullOrEmpty(stepId);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrEmpty(batchName);
        ArgumentException.ThrowIfNullOrEmpty(batchDefinitionId);

        // Read the stored record this id refers to. It was created Pending on the prior attempt; the resume
        // probe already established it exists, but it may have been DECIDED or reaped between the probe read
        // and now (a reaper sweep, or a dashboard decision landing on the recovered Pending record). Route
        // on whatever state it is in to close that race.
        var stored = await _approvalStore.GetAsync(approvalId, cancellationToken).ConfigureAwait(false);
        if (stored is { Status: ApprovalRecordStatus.Decided })
        {
            switch (stored.Outcome)
            {
                case ApprovalRecordOutcome.Approved:
                case ApprovalRecordOutcome.AutoApproved:
                    return;   // already approved before/during the race → proceed to the next step
                case ApprovalRecordOutcome.Rejected:
                case ApprovalRecordOutcome.TimedOutFail:
                case ApprovalRecordOutcome.Dismissed:
                    throw new BatchStepFailureException(
                        $"Approval {approvalId} was decided '{stored.Outcome}' before re-attach.");
                default:
                    // Cancelled / Interrupted are crash-orphan markers, not human decisions — mint a fresh
                    // gate so a real decision can still be made (mirrors the executor's Interrupted arm).
                    await AwaitApprovalAsync(batchId, stepId, config, batchName, batchDefinitionId, cancellationToken).ConfigureAwait(false);
                    return;
            }
        }
        if (stored is null)
        {
            // The record vanished between the probe read and now (it cannot be re-attached). Open a fresh
            // gate — the safe, never-skip fallback consistent with the resume-bias-toward-progress posture.
            await AwaitApprovalAsync(batchId, stepId, config, batchName, batchDefinitionId, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Still Pending → re-register an in-memory awaiter against the SAME approvalId so the operator sees
        // the one existing gate (not a second). The deadline is recomputed from the persisted DeadlineUtc so
        // the timeout continues from when the gate was originally created, not from now. No create-time
        // SaveAsync — the Pending record already exists; the terminal write-through still fires from the
        // shared lifecycle on resolution.
        using var gateCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tcs = new TaskCompletionSource<ApprovalOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = new ApprovalGateRegistration
        {
            ApprovalId = approvalId,
            BatchId = batchId,
            StepId = stepId,
            BatchName = batchName,
            BatchDefinitionId = batchDefinitionId,
            Config = config,
            PendingSinceUtc = stored.PendingSinceUtc,
            DeadlineUtc = stored.DeadlineUtc,
            Tcs = tcs,
            GateCts = gateCts,
        };
        _gates[approvalId] = registration;

        var announcement = new PendingApproval
        {
            ApprovalId = approvalId,
            BatchId = batchId,
            BatchStepId = stepId,
            BatchName = batchName,
            Config = config,
            PendingSinceUtc = stored.PendingSinceUtc,
            DeadlineUtc = stored.DeadlineUtc,
        };
        await AwaitGateOutcomeAsync(registration, gateCts, announcement, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Shared gate lifecycle used by both <see cref="AwaitApprovalAsync"/> (a fresh gate) and
    /// <see cref="ReattachApprovalAsync"/> (an existing Pending gate): announce the snapshot to the hub
    /// fan-out, arm the per-gate timeout, wire caller-cancellation, await the resolution, write the terminal
    /// outcome through the durable store exactly once, and route the outcome (approve → return; reject /
    /// timeout → throw; cancel → OCE). The caller has already put <paramref name="registration"/> in
    /// <see cref="_gates"/> (and, on the fresh path, persisted the create-time Pending record) — the
    /// ordering dict → store → channel is preserved by doing those before this call. Behavior-neutral
    /// extraction: <see cref="AwaitApprovalAsync"/> runs exactly as it did inline.
    /// </summary>
    private async Task AwaitGateOutcomeAsync(
        ApprovalGateRegistration registration,
        CancellationTokenSource gateCts,
        PendingApproval announcement,
        CancellationToken cancellationToken)
    {
        var approvalId = registration.ApprovalId;
        var tcs = registration.Tcs;
        var config = registration.Config;

        _newGates.Writer.TryWrite(announcement);

        // Per-gate timeout — guarded against negative remaining.
        Task? timeoutTask = null;
        if (registration.DeadlineUtc is { } d)
        {
            timeoutTask = Task.Run(async () =>
            {
                try
                {
                    var remaining = d - _clock.GetUtcNow();
                    if (remaining <= TimeSpan.Zero)
                    {
                        ApplyTimeout(config.OnTimeout, tcs, approvalId);
                        return;
                    }
                    await Task.Delay(remaining, gateCts.Token).ConfigureAwait(false);
                    ApplyTimeout(config.OnTimeout, tcs, approvalId);
                }
                catch (OperationCanceledException)
                {
                    // gate resolved by Approve/Reject/Cancel
                }
            }, gateCts.Token);
        }

        // Wire caller cancellation -> Tcs as Cancelled.
        using var cancelReg = cancellationToken.Register(static state =>
        {
            var tcsRef = (TaskCompletionSource<ApprovalOutcome>)state!;
            tcsRef.TrySetResult(ApprovalOutcome.Cancelled);
        }, tcs);

        try
        {
            var outcome = await tcs.Task.ConfigureAwait(false);

            // CENTRALIZED durable-record write — fires for EVERY terminal outcome (approve,
            // auto-approve, reject, timeout-fail, AND cancel) exactly once from this one place, so
            // ApproveAsync/RejectAsync stay synchronous (they only resolve the tcs + stash decision
            // metadata on `registration`). Cancellation writes a terminal `Cancelled` record
            // (decidedBy "<cancelled>", no human) so the store-aware merge never resurrects a
            // torn-down gate. The map is 1:1 with the Core ApprovalOutcome.
            await WriteOutcomeThroughAsync(registration, outcome).ConfigureAwait(false);

            switch (outcome)
            {
                case ApprovalOutcome.Approved:
                case ApprovalOutcome.AutoApproved:
                    return;
                case ApprovalOutcome.Rejected:
                    throw new BatchStepFailureException($"Approval {approvalId} rejected.");
                case ApprovalOutcome.TimedOutFail:
                    throw new BatchStepFailureException($"Approval {approvalId} timed out (Fail).");
                case ApprovalOutcome.Cancelled:
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new OperationCanceledException();
                default:
                    throw new InvalidOperationException($"Unknown ApprovalOutcome: {outcome}");
            }
        }
        finally
        {
            gateCts.Cancel();
            _gates.TryRemove(approvalId, out _);
            if (timeoutTask is not null)
            {
                try
                {
                    await timeoutTask.ConfigureAwait(false);
                }
                catch
                {
                    // best-effort
                }
            }
        }
    }

    /// <summary>
    /// Writes the terminal outcome through the durable store exactly once.
    /// Maps the Core <see cref="ApprovalOutcome"/> 1:1 to the public <see cref="ApprovalRecordOutcome"/>
    /// (<c>Interrupted</c> is reaper-only and never produced here). Attribution comes from the decision
    /// metadata <c>ApproveAsync</c>/<c>RejectAsync</c> stashed on the registration; auto-approve /
    /// timeout-fail use "&lt;system&gt;", cancellation uses "&lt;cancelled&gt;".
    /// </summary>
    /// <remarks>
    /// <b>Crash-window belt-and-suspenders:</b> if the gate was never persisted (a crash between the
    /// dict insert and the create-time <c>SaveAsync</c> — though the create-write awaits BEFORE
    /// <c>await tcs.Task</c>, so this cannot happen in-process), <c>RecordOutcomeAsync</c> would throw
    /// <see cref="ApprovalGateNotFoundException"/> into the batch's resolution path. We DOWNGRADE that
    /// SPECIFIC type to a one-line warn-log so a torn-down never-persisted gate can never crash
    /// resolution — while any OTHER <see cref="InvalidOperationException"/> from the store (e.g. a
    /// transient fault) still propagates and fails the step. The
    /// <c>EfApprovalGateStore.RecordOutcomeAsync</c> keeps its throw contract for DIRECT dashboard
    /// callers (a 404 via the typed map).
    /// </remarks>
    private async Task WriteOutcomeThroughAsync(ApprovalGateRegistration registration, ApprovalOutcome outcome)
    {
        var (mapped, decidedBy, note) = outcome switch
        {
            ApprovalOutcome.Approved => (ApprovalRecordOutcome.Approved, registration.DecidedBy ?? "<system>", registration.DecisionNote),
            ApprovalOutcome.AutoApproved => (ApprovalRecordOutcome.AutoApproved, "<system>", (string?)null),
            ApprovalOutcome.Rejected => (ApprovalRecordOutcome.Rejected, registration.DecidedBy ?? "<system>", registration.DecisionNote),
            ApprovalOutcome.TimedOutFail => (ApprovalRecordOutcome.TimedOutFail, "<system>", (string?)null),
            ApprovalOutcome.Cancelled => (ApprovalRecordOutcome.Cancelled, CancelledSentinel, (string?)null),
            _ => (ApprovalRecordOutcome.Cancelled, CancelledSentinel, (string?)null),
        };

        try
        {
            // CT-DECOUPLED (CancellationToken.None): the durable outcome record is an audit write that
            // MUST land even on the cancellation path — the caller token is already cancelled there, and
            // abandoning the write would leave the gate Pending (a ghost pending record). Mirrors the
            // JobRunner/JobScheduler CT-decoupling of terminal status writes.
            await _approvalStore.RecordOutcomeAsync(
                registration.ApprovalId, mapped, decidedBy, _clock.GetUtcNow(), note, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ApprovalAlreadyDecidedException ex)
        {
            // The gate was already terminalized (e.g. the startup reaper Interrupted it before this
            // resolution wrote through). The first terminal record wins; do not overwrite. Warn, don't crash.
            // (ApprovalAlreadyDecidedException and ApprovalGateNotFoundException are sealed siblings — both
            // derive directly from InvalidOperationException — so the order of these two arms is immaterial.)
            _logger.LogWarning(
                ex,
                "Approval gate {GateId}: outcome already recorded ({Existing}); skipping duplicate write.",
                registration.ApprovalId,
                ex.ExistingOutcome);
        }
        catch (ApprovalGateNotFoundException ex)
        {
            // Absent-gate (never-persisted crash orphan) — downgrade to warn so resolution never crashes.
            // We catch the DEDICATED type, NOT a bare InvalidOperationException: any other IOE from the
            // store (e.g. a transient DB fault) MUST propagate and fail the step honestly rather than be
            // silently swallowed, which would leave the gate Pending and hide the error.
            _logger.LogWarning(
                ex,
                "Approval gate {GateId}: durable outcome write found no record (gate may never have been persisted before a crash); skipping.",
                registration.ApprovalId);
        }
    }

    private void ApplyTimeout(ApprovalTimeoutAction action, TaskCompletionSource<ApprovalOutcome> tcs, string approvalId)
    {
        switch (action)
        {
            case ApprovalTimeoutAction.AutoApprove:
                tcs.TrySetResult(ApprovalOutcome.AutoApproved);
                break;
            case ApprovalTimeoutAction.Fail:
                tcs.TrySetResult(ApprovalOutcome.TimedOutFail);
                break;
            case ApprovalTimeoutAction.Hold:
                _logger.LogWarning(
                    "Approval gate {GateId} reached deadline; OnTimeout=Hold, gate remains pending.",
                    approvalId);
                break;
            default:
                // Unknown action — log and treat as Hold (safer than auto-resolving).
                _logger.LogWarning(
                    "Approval gate {GateId}: unknown ApprovalTimeoutAction {Action}; gate held open.",
                    approvalId,
                    action);
                break;
        }
    }

    // ===== IApprovalGateService (public, frozen Abstractions) =====

    /// <inheritdoc/>
    /// <remarks>
    /// Store-aware merge: returns the UNION of live in-memory gates (the authoritative ones with a
    /// real <c>tcs</c>) and durable store records still in
    /// <see cref="ApprovalRecordStatus.Pending"/>, deduped by <c>ApprovalId</c> with LIVE WINNING. This
    /// makes restart-recovery automatic and side-effect-free: after a process exit the in-memory dict is
    /// empty, so the dashboard sees the store's pending records (visible for AUDIT — no batch
    /// resumes). Terminalized records (cancelled / reaped to <c>Interrupted</c>) are excluded because the
    /// store's <c>ListPendingAsync</c> filters to <c>Pending</c> — no ghost gate survives more than one
    /// reaper cycle. Both sides are role-filtered identically.
    /// </remarks>
    public async Task<IReadOnlyList<PendingApproval>> ListPendingAsync(string? userRole, CancellationToken cancellationToken)
    {
        var live = _gates.Values
            .Where(g => !g.Tcs.Task.IsCompleted)
            .Select(g => new PendingApproval
            {
                ApprovalId = g.ApprovalId,
                BatchId = g.BatchId,
                BatchStepId = g.StepId,
                BatchName = g.BatchName,
                Config = g.Config,
                PendingSinceUtc = g.PendingSinceUtc,
                DeadlineUtc = g.DeadlineUtc,
            })
            .Where(p => RoleAllows(p.Config, userRole))
            .ToList();

        var liveIds = new HashSet<string>(live.Select(p => p.ApprovalId), StringComparer.Ordinal);

        // Store pending records NOT present in-memory (live wins on dedupe). The store IS the recovery
        // source — queried on demand, no rehydration push.
        var stored = await _approvalStore.ListPendingAsync(cancellationToken).ConfigureAwait(false);
        var merged = live;
        foreach (var rec in stored)
        {
            if (liveIds.Contains(rec.ApprovalId) || !RoleAllows(rec.Config, userRole))
            {
                continue;
            }
            merged.Add(new PendingApproval
            {
                ApprovalId = rec.ApprovalId,
                BatchId = rec.BatchId,
                BatchStepId = rec.BatchStepId,
                // Store-recovered path (process restarted → no live registration). Resolve the name via
                // the DEFINITION id (now correct — rec.BatchDefinitionId IS a definition id), falling
                // back to the raw id then "<unknown>". The persisted record does not carry the name.
                BatchName = (rec.BatchDefinitionId is { } defId ? _batchLookup.TryGetById(defId)?.Name : null)
                    ?? rec.BatchDefinitionId ?? "<unknown>",
                Config = rec.Config,
                PendingSinceUtc = rec.PendingSinceUtc,
                DeadlineUtc = rec.DeadlineUtc,
            });
        }

        // Stable order: PendingSinceUtc then ApprovalId.
        var ordered = merged
            .OrderBy(p => p.PendingSinceUtc)
            .ThenBy(p => p.ApprovalId, StringComparer.Ordinal)
            .ToList();
        return ordered;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Reads the durable store's by-batch records (pending AND decided) and maps each to a focused
    /// <see cref="ApprovalGateView"/>. Unlike <see cref="ListPendingAsync"/> this does NOT merge the
    /// live in-memory dict and does NOT role-filter: a status renderer needs every gate's decided
    /// outcome regardless of role, and the store is the single source of truth for decided gates
    /// (a live pending gate is also persisted as a Pending record at create time, so the store view
    /// is complete). Empty for an unknown run.
    /// </remarks>
    public async Task<IReadOnlyList<ApprovalGateView>> ListForBatchAsync(string batchId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchId);
        var records = await _approvalStore.ListByBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
        return records
            .Select(static r => new ApprovalGateView
            {
                ApprovalId = r.ApprovalId,
                BatchId = r.BatchId,
                BatchStepId = r.BatchStepId,
                Status = r.Status,
                Outcome = r.Outcome,
                DecidedAtUtc = r.DecidedAtUtc,
            })
            .ToList();
    }

    private static bool RoleAllows(ApprovalGateConfig config, string? userRole) =>
        userRole is null
        || config.AllowedRoles.Contains(userRole, StringComparer.Ordinal)
        || config.AllowedRoles.Contains(ApprovalGateConfig.AnyAuthenticatedUser, StringComparer.Ordinal);

    /// <inheritdoc/>
    public Task ApproveAsync(string approvalId, ApproverContext approver, string? note, CancellationToken cancellationToken)
    {
        // cancellationToken is intentionally NOT consumed: the decision is a synchronous tcs resolution
        // (no awaited I/O on this path) and the durable outcome write is deliberately CT-decoupled
        // (CancellationToken.None inside WriteOutcomeThroughAsync) so the audit record always lands. The
        // parameter stays for the CancellationToken-last convention; do NOT wire it to the tcs — that would
        // reintroduce the ghost-gate-on-cancel bug this design avoids.
        ArgumentException.ThrowIfNullOrEmpty(approvalId);
        ArgumentNullException.ThrowIfNull(approver);
        if (!_gates.TryGetValue(approvalId, out var gate))
        {
            // Typed exception so the endpoint can map to 404.
            throw new ApprovalNotFoundException($"Approval {approvalId} not found or already resolved.")
            {
                ApprovalId = approvalId,
            };
        }
        AuthorizeOrThrow(gate, approver);
        // Authorize BEFORE claiming: an unauthorized caller must NOT consume the single decision claim —
        // that would deadlock the gate (a later authorized caller would find the claim already taken).
        if (!gate.TryClaimDecision())
        {
            // A concurrent approve/reject already claimed this gate. That first caller owns the outcome
            // (the tcs has a single winner); this later call no-ops so it cannot overwrite the recorded
            // decider/note. Idempotent success — mirrors the prior behavior where a losing TrySetResult
            // already returned silently.
            _logger.LogInformation("Approval {Id}: decision already claimed by a concurrent caller; ignoring this approve.", approvalId);
            return Task.CompletedTask;
        }
        _logger.LogInformation("Approval {Id} approved by {User} (note: {Note})", approvalId, approver.Identity, note);
        // Stash decision attribution BEFORE resolving the tcs, so the centralized durable write in
        // AwaitApprovalAsync's resolution path records who/why. The claim above guarantees only this caller
        // writes these fields, so the resolution continuation observes a consistent (outcome, decider, note).
        gate.DecidedBy = approver.Identity;
        gate.DecisionNote = note;
        gate.Tcs.TrySetResult(ApprovalOutcome.Approved);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task RejectAsync(string approvalId, ApproverContext approver, string reason, CancellationToken cancellationToken)
    {
        // cancellationToken is intentionally NOT consumed — see the note in ApproveAsync (synchronous tcs
        // resolution; the durable write is CT-decoupled). The parameter stays for the
        // CancellationToken-last convention; do NOT wire it to the tcs.
        ArgumentException.ThrowIfNullOrEmpty(approvalId);
        ArgumentNullException.ThrowIfNull(approver);
        ArgumentException.ThrowIfNullOrEmpty(reason);
        if (!_gates.TryGetValue(approvalId, out var gate))
        {
            // Typed exception so the endpoint can map to 404.
            throw new ApprovalNotFoundException($"Approval {approvalId} not found or already resolved.")
            {
                ApprovalId = approvalId,
            };
        }
        AuthorizeOrThrow(gate, approver);
        // Authorize BEFORE claiming (see ApproveAsync) so an unauthorized caller can't deadlock the gate.
        if (!gate.TryClaimDecision())
        {
            // A concurrent approve/reject already claimed this gate; no-op so we can't overwrite the
            // recorded decider/reason. Idempotent success (see ApproveAsync).
            _logger.LogInformation("Approval {Id}: decision already claimed by a concurrent caller; ignoring this reject.", approvalId);
            return Task.CompletedTask;
        }
        _logger.LogInformation("Approval {Id} rejected by {User}: {Reason}", approvalId, approver.Identity, reason);
        // Stash decision attribution BEFORE resolving the tcs (see ApproveAsync); the claim makes this caller
        // the sole writer of these fields.
        gate.DecidedBy = approver.Identity;
        gate.DecisionNote = reason;
        gate.Tcs.TrySetResult(ApprovalOutcome.Rejected);
        return Task.CompletedTask;
    }

    private static void AuthorizeOrThrow(ApprovalGateRegistration gate, ApproverContext approver)
    {
        var allowed = gate.Config.AllowedRoles;
        if (allowed.Count == 0)
        {
            // Typed exception so the endpoint can map to 500
            // (a configuration bug, not caller fault).
            throw new ApprovalConfigInvalidException(
                $"Approval {gate.ApprovalId} has empty AllowedRoles (fail-safe deadlock).")
            {
                ApprovalId = gate.ApprovalId,
            };
        }
        // The AnyAuthenticatedUser ("*") sentinel must NOT admit anonymous callers. A naive wildcard
        // contains-check would return true regardless of authentication state, so an anonymous caller
        // (no auth scheme configured) would satisfy AllowedRoles=["*"]. Endpoints derive
        // Identity="anonymous" for unauthenticated requests (see
        // ApprovalsEndpoints.BuildApproverFromHttpContext); we treat that sentinel as anonymous and
        // gate the wildcard branch behind it.
        var isAnonymous = string.Equals(approver.Identity, "anonymous", StringComparison.Ordinal);
        var wildcardOk = !isAnonymous && allowed.Contains(ApprovalGateConfig.AnyAuthenticatedUser, StringComparer.Ordinal);
        var roleOk = allowed.Any(role => approver.Roles.Contains(role, StringComparer.Ordinal));
        if (!(wildcardOk || roleOk))
        {
            // Typed exception so the endpoint can map to 403.
            throw new ApprovalRoleMismatchException(
                $"Approver {approver.Identity} lacks any of the allowed roles.")
            {
                ApproverIdentity = approver.Identity,
                ApprovalId = gate.ApprovalId,
            };
        }
    }
}
