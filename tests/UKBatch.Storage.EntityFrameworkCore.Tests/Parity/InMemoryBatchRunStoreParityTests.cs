using Microsoft.Extensions.Time.Testing;
using UKBatch.Abstractions.Storage;
using UKBatch.Storage;
using Xunit;

namespace UKBatch.Storage.EntityFrameworkCore.Tests.Parity;

/// <summary>
/// Parity baseline: runs the shared run-store suite against the real <see cref="InMemoryBatchRunStore"/>.
/// This is the reference the EF providers must match. Docker-free.
/// </summary>
[Trait("Category", "Parity")]
public sealed class InMemoryBatchRunStoreParityTests : BatchRunStoreParityTestBase
{
    protected override Task<IBatchRunStore> CreateStoreAsync(FakeTimeProvider clock)
        => Task.FromResult<IBatchRunStore>(new InMemoryBatchRunStore());
}
