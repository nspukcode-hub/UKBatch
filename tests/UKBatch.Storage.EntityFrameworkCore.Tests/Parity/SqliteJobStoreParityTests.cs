using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using UKBatch.Abstractions.Storage;
using UKBatch.Storage;
using UKBatch.Storage.EntityFrameworkCore.Stores;
using UKBatch.Storage.EntityFrameworkCore.Tests.Infrastructure;
using Xunit;

namespace UKBatch.Storage.EntityFrameworkCore.Tests.Parity;

/// <summary>
/// Parity: runs the shared suite against the EF <see cref="EfJobStore"/> over migrated SQLite
/// (<c>:memory:</c> with a keep-alive connection — the same real-migration harness the SQLite store
/// tests use). Docker-free, so this is the CI gate that proves the EF adapter is a drop-in everywhere
/// the build runs. PostgreSQL parity is the <c>[RequiresDocker]</c> sibling.
/// </summary>
[Trait("Category", "Parity")]
public sealed class SqliteJobStoreParityTests : JobStoreParityTestBase
{
    private SqliteStoreHarness? _harness;

    protected override async Task<IJobStoreInternal> CreateStoreAsync(FakeTimeProvider clock)
    {
        _harness = await SqliteStoreHarness.CreateAsync(clock);
        return new EfJobStore(
            _harness.Factory,
            new JobExecutionWatchHub(NullLogger<JobExecutionWatchHub>.Instance),
            clock,
            NullLogger<EfJobStore>.Instance);
    }

    protected override async Task DisposeStoreAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }
}
