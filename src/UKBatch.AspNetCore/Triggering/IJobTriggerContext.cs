using Microsoft.AspNetCore.Http;
using UKBatch.Abstractions.Models;

namespace UKBatch.AspNetCore.Triggering;

/// <summary>
/// Resolves the identity that should populate <see cref="JobExecution.TriggeredBy"/> for jobs
/// triggered from inside an ASP.NET Core request scope.
/// </summary>
/// <remarks>
/// Default implementation reads <see cref="HttpContext.User"/>.<see cref="System.Security.Claims.ClaimsPrincipal.Identity"/>?.<see cref="System.Security.Principal.IIdentity.Name"/>, falling back to the
/// <c>sub</c> claim if present. Returns <c>null</c> when no HTTP context is ambient (e.g. the runtime
/// itself triggered the job from the scheduler) — callers handle null by passing it through to
/// <see cref="UKBatch.Runtime.IJobRunner.TriggerAsync"/>, which records the value as-is.
/// </remarks>
public interface IJobTriggerContext
{
    /// <summary>Returns the resolved identity, or <c>null</c> if none is available.</summary>
    string? GetTriggeredByOrNull();
}
