using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;

namespace UKBatch.Worker.Tests.Common;

/// <summary>
/// Stub <see cref="IJobDefinitionLookup"/> returning a fixed registration-order job set, so the
/// heartbeat can snapshot job names without standing up the Core registry.
/// </summary>
internal sealed class StubJobDefinitionLookup : IJobDefinitionLookup
{
    private readonly IReadOnlyList<JobDefinition> _all;

    public StubJobDefinitionLookup(params string[] jobNames)
    {
        _all = jobNames.Select(Make).ToArray();
    }

    public JobDefinition? TryGet(string jobName)
        => _all.FirstOrDefault(j => string.Equals(j.Name, jobName, StringComparison.Ordinal));

    public IReadOnlyList<JobDefinition> All() => _all;

    private static JobDefinition Make(string name) => new()
    {
        Name = name,
        ImplementationTypeName = null,
        IsPartitioned = false,
        MaxRetries = 0,
        TimeoutSeconds = 0,
        DefaultParameters = new Dictionary<string, object?>(),
        Tags = [],
    };
}
