namespace UKBatch.Abstractions.Batches;

/// <summary>
/// Read-only lookup for code-defined <see cref="BatchDefinition"/> instances
/// (those registered via <c>UKBatchBuilder.AddBatch(...)</c>; <see cref="BatchSource.Code"/>).
/// Dashboard- and API-defined batches live in <see cref="Storage.IBatchDefinitionStore"/>
/// and are NOT exposed through this lookup; both sources are composed at a higher level by
/// <see cref="Storage.IBatchCatalogService"/>. Implementations MUST be thread-safe and lock-free for reads.
/// </summary>
/// <remarks>
/// All lookups are synchronous because code-defined batches are held in-process and never
/// involve I/O. The implementation backing this interface is registered as a DI singleton
/// by <c>UKBatchBuilder.Complete()</c>.
/// <para>
/// <b>Source scope:</b> this lookup is Code-only. There is no name-keyed lookup for
/// store-defined batches here — <see cref="Storage.IBatchCatalogService"/> composes both sources
/// explicitly (backed by <c>IBatchDefinitionStore.GetByNameAsync</c>). On a hypothetical
/// Code↔Store name collision, this lookup returns ONLY the Code-defined batch; downstream
/// composition must surface or reject the ambiguity.
/// </para>
/// </remarks>
public interface IBatchDefinitionLookup
{
    /// <summary>
    /// Returns the definition whose <see cref="BatchDefinition.Name"/> equals
    /// <paramref name="name"/> (ordinal comparison), or <c>null</c> if absent.
    /// Throws <see cref="ArgumentException"/> if <paramref name="name"/> is null or empty.
    /// </summary>
    /// <remarks>
    /// Whitespace-only names are permitted at the lookup boundary (will simply miss and
    /// return <c>null</c>); they are rejected at the REGISTRATION boundary in
    /// <c>UKBatchBuilder.AddBatch(name, configure)</c> via <c>ThrowIfNullOrWhiteSpace</c>.
    /// The asymmetry is intentional: registration is a programmer-time error; lookup is
    /// runtime input that may legitimately be invalid (e.g. ill-formed REST query param).
    /// </remarks>
    BatchDefinition? TryGetByName(string name);

    /// <summary>
    /// Returns the definition whose <see cref="BatchDefinition.Id"/> equals
    /// <paramref name="id"/> (ordinal comparison), or <c>null</c> if absent.
    /// Throws <see cref="ArgumentException"/> if <paramref name="id"/> is null or empty.
    /// </summary>
    BatchDefinition? TryGetById(string id);

    /// <summary>
    /// Snapshot of every code-defined batch in REGISTRATION ORDER (the order in which
    /// <c>UKBatchBuilder.AddBatch(...)</c> was called during host setup). The returned list
    /// is a defensive copy — callers may iterate freely without affecting the registry.
    /// </summary>
    /// <remarks>
    /// Registration-order is a CONTRACT, not an implementation detail, locking down deterministic
    /// iteration. REST endpoints that need name-sorted output MUST sort the snapshot themselves,
    /// and may apply in-memory filtering on it. If the count grows beyond ~1000 entries
    /// (unlikely for code-defined batches), a streaming variant may be added.
    /// </remarks>
    IReadOnlyList<BatchDefinition> All();
}
