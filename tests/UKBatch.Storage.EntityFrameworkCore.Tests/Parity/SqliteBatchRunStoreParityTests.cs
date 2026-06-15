using Microsoft.Extensions.Time.Testing;
using UKBatch.Abstractions.Storage;
using UKBatch.Storage.EntityFrameworkCore.Stores;
using UKBatch.Storage.EntityFrameworkCore.Tests.Infrastructure;
using Xunit;

namespace UKBatch.Storage.EntityFrameworkCore.Tests.Parity;

/// <summary>
/// Parity: runs the shared run-store suite against the EF <see cref="EfBatchRunStore"/> over migrated
/// SQLite (the real-migration harness). Docker-free, so this is the CI gate proving the EF run store is a
/// drop-in everywhere the build runs. PostgreSQL parity is the <c>[RequiresDocker]</c> sibling.
/// </summary>
[Trait("Category", "Parity")]
public sealed class SqliteBatchRunStoreParityTests : BatchRunStoreParityTestBase
{
    private SqliteStoreHarness? _harness;

    protected override async Task<IBatchRunStore> CreateStoreAsync(FakeTimeProvider clock)
    {
        _harness = await SqliteStoreHarness.CreateAsync(clock);
        return new EfBatchRunStore(_harness.Factory);
    }

    protected override async Task DisposeStoreAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }
}
