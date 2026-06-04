using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using UKBatch.Transport.Http.Auth;
using UKBatch.Transport.Http.Endpoints;

namespace UKBatch.Transport.Http;

/// <summary>Endpoint-mapping helpers for the HTTP transport receiver surface.</summary>
public static class EndpointRouteBuilderExtensions
{
    /// <summary>
    /// Fixed wire-protocol mount path. NOT caller-configurable — every UKBatch worker exposes
    /// the same prefix so cross-service discovery is unambiguous.
    /// </summary>
    public const string MountPath = "/ukbatch/internal/jobs";

    /// <summary>
    /// Mounts the three receiver endpoints under <see cref="MountPath"/>:
    /// <list type="bullet">
    ///   <item><c>POST /ukbatch/internal/jobs/publish</c> — fire-and-forget message publish.</item>
    ///   <item><c>GET  /ukbatch/internal/jobs/poll?topic={t}&amp;waitMs={ms}</c> — long-poll subscribe.</item>
    ///   <item><c>POST /ukbatch/internal/jobs/invoke</c> — synchronous request/reply invocation.</item>
    /// </list>
    /// All three endpoints are protected by the <see cref="HmacAuthorizationFilter"/>; requests
    /// missing or failing the three signed headers are rejected with 401 + ProblemDetails.
    /// </summary>
    /// <remarks>
    /// <para><b>Anti-forgery exemption:</b> the three endpoints opt out of ASP.NET Core's
    /// antiforgery (cross-service calls aren't browser-form-posted). Receiver-side anti-forgery is
    /// enforced via HMAC signature + nonce + timestamp — stronger than anti-forgery tokens.</para>
    /// <para><b>Cache-Control:</b> every response carries <c>Cache-Control: no-store</c> +
    /// <c>Pragma: no-cache</c> regardless of handler outcome. Long-poll responses MUST NOT be
    /// cached by intermediaries (nginx, CDN, browser). Filter ordering puts
    /// Cache-Control FIRST so the headers are applied unconditionally — even when the HMAC
    /// filter short-circuits with a 401 (auth fail / replay / clock skew) or 413 (body too large),
    /// the response carries the headers. The endpoint filter chain executes in registration order:
    /// Cache-Control runs first, sets headers, then calls <c>next(ctx)</c> → HMAC filter runs
    /// second. If HMAC returns directly without calling <c>next</c>, the Cache-Control headers were
    /// already set on the response.</para>
    /// </remarks>
    public static IEndpointRouteBuilder MapUKBatchHttpTransport(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(MountPath)
            .AddEndpointFilter(async (ctx, next) =>
            {
                // Set cache-suppression headers BEFORE the HMAC filter runs.
                // ASP.NET Core minimal-API endpoint filter chain executes in registration order;
                // when HmacAuthorizationFilter returns directly (e.g. 401 / 413) without calling
                // next(ctx), the chain short-circuits. By registering Cache-Control FIRST, headers
                // are already applied on the response by the time HMAC fails — applied
                // unconditionally regardless of the HMAC verify outcome.
                ctx.HttpContext.Response.Headers.CacheControl = "no-store";
                ctx.HttpContext.Response.Headers.Pragma = "no-cache";
                return await next(ctx).ConfigureAwait(false);
            })
            .AddEndpointFilter<HmacAuthorizationFilter>()
            .DisableAntiforgery();

        group.MapPost("/publish", PublishEndpointHandler.HandleAsync)
            .WithName("UKBatchInternalPublish")
            .WithSummary("Fire-and-forget message publish (internal transport).")
            .ExcludeFromDescription();

        group.MapGet("/poll", PollEndpointHandler.HandleAsync)
            .WithName("UKBatchInternalPoll")
            .WithSummary("Long-poll subscribe for inbound messages (internal transport).")
            .ExcludeFromDescription();

        group.MapPost("/invoke", InvokeEndpointHandler.HandleAsync)
            .WithName("UKBatchInternalInvoke")
            .WithSummary("Synchronous request/reply invocation (internal transport).")
            .ExcludeFromDescription();

        return endpoints;
    }
}
