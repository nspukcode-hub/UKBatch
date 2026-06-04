namespace UKBatch.Api.Jobs;

/// <summary>Body for <c>POST /jobs/{name}/trigger</c>. Optional.</summary>
public sealed record class JobTriggerRequest
{
    /// <summary>Caller-supplied parameters merged with the job's defaults.</summary>
    public IReadOnlyDictionary<string, object?>? Parameters { get; init; }

    /// <summary>
    /// Optional identity to attribute the trigger to. If absent, the endpoint falls back to
    /// <c>IJobTriggerContext.GetTriggeredByOrNull()</c> (i.e. <c>HttpContext.User.Identity.Name</c>).
    /// </summary>
    public string? TriggeredBy { get; init; }
}
