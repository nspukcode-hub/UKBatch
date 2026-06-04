using System.Net;

namespace UKBatch.Dashboard.Clients;

/// <summary>
/// Thrown by <see cref="IUKBatchClient"/> implementations on REST error responses (non-2xx with
/// a ProblemDetails body, OR 5xx, OR transport failure). Captures the Problem Details
/// <c>type</c> URI so page components can typed-catch on <c>ProblemType</c> instead of brittle
/// HTTP status code matches.
/// </summary>
/// <remarks>
/// Maps the 11 ProblemDetails URIs (<c>ukbatch:job-not-registered</c>,
/// <c>:batch-not-found</c>, <c>:execution-not-found</c>, <c>:approval-not-pending</c>,
/// <c>:forbidden</c>, <c>:approval-config-invalid</c>, <c>:validation-failed</c>,
/// <c>:concurrency-conflict</c>, <c>:not-acceptable-state</c>, <c>:batch-definition-not-found</c>,
/// <c>:batch-definition-duplicate-name</c>) to typed properties.
/// </remarks>
public sealed class UKBatchClientException : Exception
{
    /// <summary>Constructs with a structured ProblemDetails payload.</summary>
    public UKBatchClientException(string message, HttpStatusCode statusCode, string? problemType = null, string? detail = null, IReadOnlyDictionary<string, string[]>? validationErrors = null)
        : base(message)
    {
        StatusCode = statusCode;
        ProblemType = problemType;
        Detail = detail;
        ValidationErrors = validationErrors;
    }

    /// <summary>Constructs with a transport-level inner exception (e.g. <see cref="HttpRequestException"/>).</summary>
    public UKBatchClientException(string message, HttpStatusCode statusCode, Exception innerException)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    /// <summary>HTTP status code returned by the service.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>ProblemDetails <c>type</c> URI (e.g. <c>ukbatch:batch-definition-not-found</c>); <c>null</c> when not a ProblemDetails response.</summary>
    public string? ProblemType { get; }

    /// <summary>ProblemDetails <c>detail</c> string; <c>null</c> when not provided.</summary>
    public string? Detail { get; }

    /// <summary>Populated for 422 / validation failures — maps field name to error messages.</summary>
    public IReadOnlyDictionary<string, string[]>? ValidationErrors { get; }
}
