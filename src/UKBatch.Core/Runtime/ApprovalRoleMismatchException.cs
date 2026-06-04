namespace UKBatch.Runtime;

/// <summary>
/// Thrown by <c>ApprovalGateService.ApproveAsync</c> / <c>RejectAsync</c> when the approver's
/// roles do not satisfy the gate's <c>ApprovalGateConfig.AllowedRoles</c>. Consumed by
/// REST endpoints for the 403 <c>ukbatch:forbidden</c> mapping.
/// </summary>
/// <remarks>
/// Inherits <see cref="InvalidOperationException"/> for zero-test-churn.
/// </remarks>
public sealed class ApprovalRoleMismatchException : InvalidOperationException
{
    /// <summary>The approver identity that was rejected; <c>null</c> if the caller did not supply context.</summary>
    public string? ApproverIdentity { get; init; }

    /// <summary>The approval id whose gate config rejected the approver; <c>null</c> if not supplied.</summary>
    public string? ApprovalId { get; init; }

    /// <summary>Constructs an exception with the given message.</summary>
    public ApprovalRoleMismatchException(string message) : base(message) { }

    /// <summary>Constructs an exception with the given message and inner exception.</summary>
    public ApprovalRoleMismatchException(string message, Exception innerException) : base(message, innerException) { }
}
