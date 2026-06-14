namespace UKBatch.Transport.Http.Auth;

/// <summary>
/// Canonical HMAC + transport header names. Single source so the signing side and the verifying
/// side cannot drift: a name mismatch silently breaks signature verification, surfacing as a 401.
/// </summary>
internal static class HmacHeaderNames
{
    public const string Signature = "X-UKBatch-Signature";
    public const string Timestamp = "X-UKBatch-Timestamp";
    public const string Nonce = "X-UKBatch-Nonce";
    public const string TimeoutMs = "X-UKBatch-Timeout-Ms";
}
