using UKBatch.Dashboard.Configuration;

namespace UKBatch.Dashboard.Models;

/// <summary>Per-service summary shown on the Landing page — health + counts + optional load error.</summary>
public sealed record class ServiceHealthViewModel
{
    /// <summary>Descriptor of the underlying service.</summary>
    public required UKBatchServiceDescriptor Service { get; init; }

    /// <summary>Current client connection state — drives the health dot color.</summary>
    public required UKBatchClientState State { get; init; }

    /// <summary>Number of registered jobs reported by the service. <c>0</c> when unknown.</summary>
    public int JobsCount { get; init; }

    /// <summary>Number of registered batch definitions. <c>0</c> when unknown.</summary>
    public int BatchesCount { get; init; }

    /// <summary>Number of pending approvals. <c>0</c> when unknown.</summary>
    public int ApprovalsCount { get; init; }

    /// <summary>Diagnostic message when one or more fetch calls failed.</summary>
    public string? LoadError { get; init; }

    /// <summary><c>true</c> when at least one fetch failed.</summary>
    public bool HasLoadError => LoadError is not null;
}
