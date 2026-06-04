using Microsoft.EntityFrameworkCore;

namespace UKBatch.Storage.EntityFrameworkCore;

/// <summary>
/// Upcast facade adapting a provider-specific pooled <c>IDbContextFactory&lt;T&gt;</c> (where
/// <typeparamref name="T"/> is <see cref="PostgresUKBatchDbContext"/> or
/// <see cref="SqliteUKBatchDbContext"/>) to <c>IDbContextFactory&lt;UKBatchDbContext&gt;</c> so the EF
/// stores stay coded against the base context type while DI registers the per-provider subclass factory
/// (per-provider migration fallback).
/// </summary>
/// <remarks>
/// <para><b>Why a facade:</b> <c>IDbContextFactory&lt;TContext&gt;</c> is INVARIANT in
/// <c>TContext</c>, so <c>IDbContextFactory&lt;PostgresUKBatchDbContext&gt;</c> is NOT assignable to
/// <c>IDbContextFactory&lt;UKBatchDbContext&gt;</c>. This facade forwards both factory members to the
/// inner subclass factory and returns the instance as the base type — an UPCAST
/// (<c>T : UKBatchDbContext</c>), always safe, no downcast.</para>
/// <para><b>Pooling unaffected:</b> the inner pooled <c>DbContext</c> subclass instance returns to its
/// own pool via its own <c>DisposeAsync</c> — which the store's <c>await using</c> invokes on the
/// concrete instance regardless of the <see cref="UKBatchDbContext"/> static type the store holds.</para>
/// </remarks>
internal sealed class SubclassFactoryFacade<T> : IDbContextFactory<UKBatchDbContext>
    where T : UKBatchDbContext
{
    private readonly IDbContextFactory<T> _inner;

    public SubclassFactoryFacade(IDbContextFactory<T> inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <inheritdoc/>
    public UKBatchDbContext CreateDbContext() => _inner.CreateDbContext();

    /// <inheritdoc/>
    public async Task<UKBatchDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => await _inner.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
}
