namespace UKBatch.Api.Common;

/// <summary>
/// Endpoint metadata carrying the endpoint's <see cref="UKBatchAccessKind"/>. Added by
/// <c>WithUKBatchAccess</c>; read by the <c>RequireUKBatchRoleAuthorization</c> convention. No
/// convention reads it unless the host opts in, so its presence alone changes nothing.
/// </summary>
internal sealed record UKBatchEndpointAccessMetadata(UKBatchAccessKind Kind);
