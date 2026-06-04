namespace UKBatch.Abstractions.Batches;

/// <summary>
/// Where the <see cref="BatchDefinition"/> originated. Source determines whether the dashboard
/// renders edit affordances and whether the store persists the definition.
/// </summary>
public enum BatchSource
{
    /// <summary>Registered programmatically via the builder API; read-only in dashboard; not persisted by storage adapters.</summary>
    Code = 0,

    /// <summary>Created via the dashboard wizard; fully editable; persisted by <see cref="Storage.IBatchDefinitionStore"/>.</summary>
    Dashboard = 1,

    /// <summary>Created via the REST API; fully editable; persisted by <see cref="Storage.IBatchDefinitionStore"/>.</summary>
    Api = 2,
}
