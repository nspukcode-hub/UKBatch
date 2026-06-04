using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using UKBatch.Abstractions.Storage;
using UKBatch.Storage;
using Xunit;

namespace UKBatch.Storage.EntityFrameworkCore.Tests.Parity;

/// <summary>
/// Parity baseline: runs the shared suite against the REAL <see cref="InMemoryJobStore"/>. This is the
/// reference the EF providers must match — if a scenario is wrong here, it is wrong for everyone, so the
/// EF subclasses inherit a trustworthy oracle. Docker-free.
/// </summary>
[Trait("Category", "Parity")]
public sealed class InMemoryJobStoreParityTests : JobStoreParityTestBase
{
    protected override Task<IJobStoreInternal> CreateStoreAsync(FakeTimeProvider clock)
    {
        var store = new InMemoryJobStore(
            clock,
            Options.Create(new UKBatchOptions()),
            new JobExecutionWatchHub(NullLogger<JobExecutionWatchHub>.Instance));
        return Task.FromResult<IJobStoreInternal>(store);
    }
}
