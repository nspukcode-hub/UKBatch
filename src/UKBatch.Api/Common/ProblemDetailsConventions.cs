namespace UKBatch.Api.Common;

/// <summary>
/// Stable Problem Details <c>type</c> URLs and titles used across the REST surface. Consumers
/// can identify the failure class deterministically (instead of parsing the message).
/// </summary>
public static class ProblemDetailsConventions
{
    /// <summary>Prefix shared by every UKBatch Problem Details <c>type</c> URL.</summary>
    public const string TypePrefix = "ukbatch:";

    /// <summary><c>ukbatch:batch-not-found</c> — 404.</summary>
    public const string BatchNotFound = TypePrefix + "batch-not-found";

    /// <summary><c>ukbatch:job-not-registered</c> — 404.</summary>
    public const string JobNotRegistered = TypePrefix + "job-not-registered";

    /// <summary><c>ukbatch:execution-not-found</c> — 404.</summary>
    public const string ExecutionNotFound = TypePrefix + "execution-not-found";

    /// <summary><c>ukbatch:approval-not-pending</c> — 404.</summary>
    public const string ApprovalNotPending = TypePrefix + "approval-not-pending";

    /// <summary><c>ukbatch:forbidden</c> — 403.</summary>
    public const string Forbidden = TypePrefix + "forbidden";

    /// <summary><c>ukbatch:approval-config-invalid</c> — 500.</summary>
    public const string ApprovalConfigInvalid = TypePrefix + "approval-config-invalid";

    /// <summary><c>ukbatch:validation-failed</c> — 400.</summary>
    public const string ValidationFailed = TypePrefix + "validation-failed";

    /// <summary><c>ukbatch:concurrency-conflict</c> — 409.</summary>
    public const string ConcurrencyConflict = TypePrefix + "concurrency-conflict";

    /// <summary><c>ukbatch:not-acceptable-state</c> — 400.</summary>
    public const string NotAcceptableState = TypePrefix + "not-acceptable-state";

    /// <summary><c>ukbatch:batch-definition-not-found</c> — 404.</summary>
    public const string BatchDefinitionNotFound = TypePrefix + "batch-definition-not-found";

    /// <summary><c>ukbatch:batch-definition-duplicate-name</c> — 409.</summary>
    public const string BatchDefinitionDuplicateName = TypePrefix + "batch-definition-duplicate-name";
}
