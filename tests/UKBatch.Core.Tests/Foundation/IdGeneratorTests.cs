using FluentAssertions;
using UKBatch.Internal;
using Xunit;

namespace UKBatch.Core.Tests.Foundation;

/// <summary>
/// UUIDv7 monotonicity and format checks. UUIDv7 IDs are k-sortable in time — strictly better
/// than V4 for storage adapter ordering.
/// </summary>
public class IdGeneratorTests
{
    [Fact]
    public void NewExecutionId_ReturnsThirtyTwoCharHexString()
    {
        var id = IdGenerator.NewExecutionId();
        id.Should().HaveLength(32);
        id.Should().MatchRegex("^[0-9a-f]{32}$");
    }

    [Fact]
    public void NewBatchId_ReturnsThirtyTwoCharHexString()
    {
        var id = IdGenerator.NewBatchId();
        id.Should().HaveLength(32);
        id.Should().MatchRegex("^[0-9a-f]{32}$");
    }

    [Fact]
    public void NewStepId_ReturnsThirtyTwoCharHexString()
    {
        var id = IdGenerator.NewStepId();
        id.Should().HaveLength(32);
        id.Should().MatchRegex("^[0-9a-f]{32}$");
    }

    [Fact]
    public void NewApprovalId_ReturnsThirtyTwoCharHexString()
    {
        var id = IdGenerator.NewApprovalId();
        id.Should().HaveLength(32);
        id.Should().MatchRegex("^[0-9a-f]{32}$");
    }

    [Fact]
    public void NewExecutionId_ConsecutiveCalls_AreUnique()
    {
        var ids = Enumerable.Range(0, 100).Select(_ => IdGenerator.NewExecutionId()).ToList();
        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void NewExecutionId_ConsecutiveCallsInSameMillisecond_AreLexSortable()
    {
        // UUIDv7 has 48-bit ms timestamp prefix; ids produced within the same ms preserve
        // lex order from the lower-order pseudo-random component. We don't strictly require
        // monotonicity within a ms (collision-resistant random) but the timestamp prefix
        // must be non-decreasing.
        var ids = new List<string>();
        for (var i = 0; i < 1000; i++)
        {
            ids.Add(IdGenerator.NewExecutionId());
        }
        // Extract the 12-hex-char timestamp prefix and confirm it's non-decreasing.
        var prefixes = ids.Select(id => id[..12]).ToList();
        for (var i = 1; i < prefixes.Count; i++)
        {
            string.CompareOrdinal(prefixes[i - 1], prefixes[i]).Should().BeLessOrEqualTo(
                0,
                $"UUIDv7 timestamp prefix should be non-decreasing across consecutive calls; saw {prefixes[i - 1]} then {prefixes[i]}");
        }
    }

    [Fact]
    public void NewExecutionId_DifferentTypes_AllProduceValidGuids()
    {
        // Sanity — every helper produces a parseable Guid.
        Guid.Parse(IdGenerator.NewExecutionId()).Should().NotBe(Guid.Empty);
        Guid.Parse(IdGenerator.NewBatchId()).Should().NotBe(Guid.Empty);
        Guid.Parse(IdGenerator.NewStepId()).Should().NotBe(Guid.Empty);
        Guid.Parse(IdGenerator.NewApprovalId()).Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task NewExecutionId_Concurrent_AllUnique()
    {
        // Stress-test concurrent generation (Guid.CreateVersion7 is thread-safe).
        const int N = 10_000;
        var ids = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Task.WhenAll(Enumerable.Range(0, N).Select(_ => Task.Run(() => ids.Add(IdGenerator.NewExecutionId())))).ConfigureAwait(false);
        ids.Distinct().Count().Should().Be(N);
    }
}
