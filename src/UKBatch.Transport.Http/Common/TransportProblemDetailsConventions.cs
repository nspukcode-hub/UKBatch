namespace UKBatch.Transport.Http.Common;

/// <summary>
/// Stable RFC 7807 <c>type</c> URIs for the HTTP transport receiver surface.
/// Mirrors the <c>ukbatch:</c> prefix used by <c>UKBatch.Api.Common.ProblemDetailsConventions</c>.
/// </summary>
public static class TransportProblemDetailsConventions
{
    /// <summary>Type prefix shared with <c>UKBatch.Api</c>'s ProblemDetails conventions.</summary>
    public const string TypePrefix = "ukbatch:";

    /// <summary>
    /// <c>ukbatch:transport-auth-failed</c> — 401. Signature invalid, missing header, OR replay
    /// nonce hit (OWASP fold per A13 — all three causes share one URI to prevent info leakage).
    /// </summary>
    public const string TransportAuthFailed = TypePrefix + "transport-auth-failed";

    /// <summary>
    /// <c>ukbatch:transport-clock-skew</c> — 401. Timestamp outside
    /// <see cref="HttpTransportOptions.MaxClockSkew"/> window. Differentiated from
    /// <see cref="TransportAuthFailed"/> so operators can grep NTP drift signals.
    /// </summary>
    public const string TransportClockSkew = TypePrefix + "transport-clock-skew";

    /// <summary>
    /// <c>ukbatch:transport-unknown-service</c> — 400 (sender-side). Caller passed a service name
    /// not present in <see cref="HttpTransportOptions.Services"/>.
    /// </summary>
    public const string TransportUnknownService = TypePrefix + "transport-unknown-service";
}
