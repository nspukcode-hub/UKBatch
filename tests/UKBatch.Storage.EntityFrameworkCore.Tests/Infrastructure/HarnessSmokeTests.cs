using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using UKBatch.Storage.EntityFrameworkCore.Tests.Infrastructure;
using Xunit;

namespace UKBatch.Storage.EntityFrameworkCore.Tests.Infrastructure;

/// <summary>Sanity check that the SQLite harness migrates and the facade upcast resolves.</summary>
public sealed class HarnessSmokeTests
{
    [Fact]
    public async Task Harness_Migrates_AllThreeTablesPresent()
    {
        await using var harness = await SqliteStoreHarness.CreateAsync();
        await using var db = await harness.NewContextAsync();

        var tables = await db.Database
            .SqlQueryRaw<string>("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")
            .ToListAsync();

        tables.Should().Contain("JobExecutions");
        tables.Should().Contain("BatchDefinitions");
        tables.Should().Contain("ApprovalGates");
    }
}
