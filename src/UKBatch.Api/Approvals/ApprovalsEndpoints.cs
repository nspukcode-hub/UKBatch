using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using UKBatch.Abstractions.Storage;
using UKBatch.Api.Common;
using UKBatch.Runtime;

namespace UKBatch.Api.Approvals;

/// <summary>Handlers for the <c>/approvals/*</c> surface.</summary>
internal static class ApprovalsEndpoints
{
    /// <summary>Maps the Approvals endpoints onto the given route group.</summary>
    public static void Map(RouteGroupBuilder group) => Map(group, operationIdPrefix: null);

    /// <summary>Maps the Approvals endpoints with an optional operation-id prefix for dual-mount scenarios.</summary>
    public static void Map(RouteGroupBuilder group, string? operationIdPrefix)
    {
        ArgumentNullException.ThrowIfNull(group);
        var approvals = group.MapGroup("/approvals").WithTags("Approvals");

        approvals.MapGet("/", async (
                IApprovalGateService svc,
                [FromQuery] string? role,
                CancellationToken ct) =>
            {
                var pending = await svc.ListPendingAsync(role, ct).ConfigureAwait(false);
                return Results.Ok(new PageEnvelope<PendingApprovalDto>
                {
                    Items = pending.Select(PendingApprovalDto.FromModel).ToList(),
                    TotalCount = pending.Count,
                    Offset = 0,
                    Limit = pending.Count,
                });
            })
            .WithUKBatchName(operationIdPrefix, "ListApprovals")
            .WithSummary("Lists currently pending approval gates; optional role filter narrows to those the caller can act on.");

        approvals.MapPost("/{id}/approve", async (
                string id,
                ApprovalNoteRequest? body,
                IApprovalGateService svc,
                HttpContext http,
                IOptions<UKBatchOptions> options,
                CancellationToken ct) =>
            {
                ArgumentException.ThrowIfNullOrEmpty(id);
                // Body may be {} with no note; body itself may be null when the client sends no body.
                var note = body?.Note;
                var approver = BuildApproverFromHttpContext(http, options.Value);
                try
                {
                    await svc.ApproveAsync(id, approver, note, ct).ConfigureAwait(false);
                    return Results.NoContent();
                }
                catch (ApprovalNotFoundException ex)
                {
                    return Results.Problem(
                        type: ProblemDetailsConventions.ApprovalNotPending,
                        statusCode: StatusCodes.Status404NotFound,
                        title: "Approval not pending",
                        detail: ex.Message);
                }
                catch (ApprovalRoleMismatchException ex)
                {
                    return Results.Problem(
                        type: ProblemDetailsConventions.Forbidden,
                        statusCode: StatusCodes.Status403Forbidden,
                        title: "Forbidden",
                        detail: ex.Message);
                }
                catch (ApprovalConfigInvalidException ex)
                {
                    return Results.Problem(
                        type: ProblemDetailsConventions.ApprovalConfigInvalid,
                        statusCode: StatusCodes.Status500InternalServerError,
                        title: "Approval config invalid",
                        detail: ex.Message);
                }
                catch (ApprovalAlreadyDecidedException ex)
                {
                    return Results.Problem(
                        type: ProblemDetailsConventions.ApprovalAlreadyDecided,
                        statusCode: StatusCodes.Status409Conflict,
                        title: "Approval already decided",
                        detail: ex.Message);
                }
            })
            .WithUKBatchName(operationIdPrefix, "ApproveApproval")
            .WithSummary("Approves a pending gate. Approver identity is derived from HttpContext.User; the request body has NO approver field.");

        approvals.MapPost("/{id}/reject", async (
                string id,
                ApprovalReasonRequest? body,
                IApprovalGateService svc,
                HttpContext http,
                IOptions<UKBatchOptions> options,
                CancellationToken ct) =>
            {
                ArgumentException.ThrowIfNullOrEmpty(id);
                if (body is null || string.IsNullOrWhiteSpace(body.Reason))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["reason"] = ["Reason is required for reject."],
                    });
                }
                var approver = BuildApproverFromHttpContext(http, options.Value);
                try
                {
                    await svc.RejectAsync(id, approver, body.Reason, ct).ConfigureAwait(false);
                    return Results.NoContent();
                }
                catch (ApprovalNotFoundException ex)
                {
                    return Results.Problem(
                        type: ProblemDetailsConventions.ApprovalNotPending,
                        statusCode: StatusCodes.Status404NotFound,
                        title: "Approval not pending",
                        detail: ex.Message);
                }
                catch (ApprovalRoleMismatchException ex)
                {
                    return Results.Problem(
                        type: ProblemDetailsConventions.Forbidden,
                        statusCode: StatusCodes.Status403Forbidden,
                        title: "Forbidden",
                        detail: ex.Message);
                }
                catch (ApprovalConfigInvalidException ex)
                {
                    return Results.Problem(
                        type: ProblemDetailsConventions.ApprovalConfigInvalid,
                        statusCode: StatusCodes.Status500InternalServerError,
                        title: "Approval config invalid",
                        detail: ex.Message);
                }
                catch (ApprovalAlreadyDecidedException ex)
                {
                    return Results.Problem(
                        type: ProblemDetailsConventions.ApprovalAlreadyDecided,
                        statusCode: StatusCodes.Status409Conflict,
                        title: "Approval already decided",
                        detail: ex.Message);
                }
            })
            .WithUKBatchName(operationIdPrefix, "RejectApproval")
            .WithSummary("Rejects a pending gate. Reason is required. Approver identity is derived from HttpContext.User.");
    }

    /// <summary>
    /// Constructs the <see cref="ApproverContext"/> EXCLUSIVELY from the HttpContext.
    /// Never reads the request body. Anonymous fallback when auth is off or the user is unauthenticated.
    /// </summary>
    /// <remarks>
    /// Enumerates EVERY configured claim type in
    /// <see cref="UKBatchOptions.ApprovalRoleClaimTypes"/> (default <c>[ClaimTypes.Role]</c>) and
    /// dedupes role values via <see cref="StringComparer.Ordinal"/>. Custom IdentityServer /
    /// Azure AD / SAML schemes configure additional types via <c>appsettings.json</c>.
    /// </remarks>
    private static ApproverContext BuildApproverFromHttpContext(HttpContext http, UKBatchOptions options)
    {
        var isAuthenticated = http.User.Identity?.IsAuthenticated == true;
        var identity = isAuthenticated
            ? (http.User.Identity!.Name ?? "anonymous")
            : "anonymous";

        // Harvest roles ONLY from an authenticated principal. An unauthenticated caller therefore
        // always yields an empty role set, so the role-based authorization path can never admit it —
        // the wildcard ("*") rejection of anonymous callers is then matched structurally by the role
        // path, not merely by the identity-string check. A host auth scheme that attaches role claims
        // to a principal whose Identity reports IsAuthenticated == false cannot reach a role-gated gate.
        var roles = new List<string>();
        if (isAuthenticated)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var claimType in options.ApprovalRoleClaimTypes)
            {
                foreach (var claim in http.User.FindAll(claimType))
                {
                    if (seen.Add(claim.Value))
                    {
                        roles.Add(claim.Value);
                    }
                }
            }
        }
        return new ApproverContext { Identity = identity, Roles = roles };
    }
}
