namespace UKBatch.Abstractions.Batches;

/// <summary>Configuration for an approval gate step.</summary>
public sealed record class ApprovalGateConfig
{
    /// <summary>
    /// Sentinel role string meaning "any authenticated user can approve". Match-by-string in the
    /// authorizer; do not combine with other role names (the sentinel takes precedence).
    /// </summary>
    public const string AnyAuthenticatedUser = "*";

    /// <summary>Heading shown in the dashboard pending-approval list.</summary>
    public required string Title { get; init; }

    /// <summary>Long-form description; supports plain text in MVP.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// ASP.NET Core role names allowed to approve or reject. Fail-safe semantics: an empty list
    /// means NO ONE can approve (the gate is dead-locked until reconfigured); use the
    /// <see cref="AnyAuthenticatedUser"/> sentinel to explicitly opt in to authenticated-any-role.
    /// Role comparison is case-SENSITIVE (ordinal) — unlike
    /// <see cref="System.Security.Principal.IPrincipal.IsInRole"/>, which ignores case. Configure
    /// role names with the exact casing your identity provider emits in the role claims.
    /// </summary>
    public required IReadOnlyList<string> AllowedRoles { get; init; }

    /// <summary>Optional wall-clock timeout. <c>null</c> means wait indefinitely.</summary>
    public TimeSpan? TimeoutAfter { get; init; }

    /// <summary>Behaviour when <see cref="TimeoutAfter"/> elapses without action. Defaults to
    /// <see cref="ApprovalTimeoutAction.Fail"/> (fail the batch on timeout) when not specified.</summary>
    public ApprovalTimeoutAction OnTimeout { get; init; } = ApprovalTimeoutAction.Fail;
}
