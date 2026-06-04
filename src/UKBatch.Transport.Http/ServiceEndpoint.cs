namespace UKBatch.Transport.Http;

/// <summary>
/// Per-service endpoint metadata. Bound under
/// <see cref="HttpTransportOptions.Services"/> per logical service name.
/// </summary>
public sealed record class ServiceEndpoint
{
    /// <summary>
    /// Absolute base URL of the target service, ending at the host (NOT including the
    /// <c>/ukbatch/internal/jobs/*</c> mount prefix — that prefix is fixed by the
    /// <see cref="HttpTransport"/> wire contract).
    /// </summary>
    /// <remarks>
    /// Validator enforces <see cref="Uri.IsAbsoluteUri"/> and HTTP/HTTPS scheme.
    /// </remarks>
    public required Uri BaseUrl { get; init; }

    /// <summary>Optional diagnostic tag (e.g. <c>"billing-prod"</c>). Surfaced in logs.</summary>
    public string? Tag { get; init; }
}
