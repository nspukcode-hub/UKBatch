namespace UKBatch.Runtime;

/// <summary>
/// Thrown by <c>ApprovalGateService.ApproveAsync</c> / <c>RejectAsync</c> when the referenced
/// approval id does not exist or has already been resolved. Consumed by REST endpoints
/// for the 404 <c>ukbatch:approval-not-pending</c> mapping.
/// </summary>
/// <remarks>
/// Inherits <see cref="InvalidOperationException"/> for zero-test-churn.
/// </remarks>
public sealed class ApprovalNotFoundException : InvalidOperationException
{
    /// <summary>The missing approval id; <c>null</c> if the caller did not supply context.</summary>
    public string? ApprovalId { get; init; }

    /// <summary>Constructs an exception with the given message.</summary>
    public ApprovalNotFoundException(string message) : base(message) { }

    /// <summary>Constructs an exception with the given message and inner exception.</summary>
    public ApprovalNotFoundException(string message, Exception innerException) : base(message, innerException) { }
}
