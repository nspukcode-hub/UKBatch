namespace UKBatch.Runtime;

/// <summary>
/// Thrown by <c>ApprovalGateService.ApproveAsync</c> / <c>RejectAsync</c> when the gate's
/// <c>ApprovalGateConfig.AllowedRoles</c> list is empty (the fail-safe deadlock state — nobody
/// can approve). Consumed by REST endpoints for the 500
/// <c>ukbatch:approval-config-invalid</c> mapping (a configuration bug, not caller fault).
/// </summary>
/// <remarks>
/// Inherits <see cref="InvalidOperationException"/> for zero-test-churn.
/// </remarks>
public sealed class ApprovalConfigInvalidException : InvalidOperationException
{
    /// <summary>The misconfigured approval id; <c>null</c> if the caller did not supply context.</summary>
    public string? ApprovalId { get; init; }

    /// <summary>Constructs an exception with the given message.</summary>
    public ApprovalConfigInvalidException(string message) : base(message) { }

    /// <summary>Constructs an exception with the given message and inner exception.</summary>
    public ApprovalConfigInvalidException(string message, Exception innerException) : base(message, innerException) { }
}
