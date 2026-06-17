namespace UKBatch.Abstractions.Storage;

/// <summary>
/// Thrown by <see cref="IApprovalGateStore.RecordOutcomeAsync"/> when NO gate record exists for the given
/// id. For a DIRECT caller (e.g. a dashboard approve of a truly-missing id) this maps to 404; the runtime
/// resolution path downgrades it to a warn-log for its never-persisted-crash-orphan case.
/// </summary>
/// <remarks>
/// <para>This is a DEDICATED type (not a bare <see cref="InvalidOperationException"/>) precisely so the
/// runtime resolution path can catch the absent-gate signal WITHOUT also swallowing an unrelated
/// <see cref="InvalidOperationException"/> raised by the store internals — for example a transient
/// store/DB fault. Swallowing such a fault would silently leave the gate <c>Pending</c> and hide the
/// error; letting it propagate fails the step honestly.</para>
/// <para>Inherits <see cref="InvalidOperationException"/> so existing 4xx mapping and test setups are
/// unaffected.</para>
/// </remarks>
public sealed class ApprovalGateNotFoundException : InvalidOperationException
{
    /// <summary>The gate id that was not found.</summary>
    public string? ApprovalId { get; init; }

    /// <summary>Constructs the exception.</summary>
    public ApprovalGateNotFoundException(string message) : base(message) { }
}
