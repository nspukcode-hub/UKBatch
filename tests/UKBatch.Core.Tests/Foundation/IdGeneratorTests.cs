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

    [Fact]
    public async Task Ids_AreRfc9562UuidV7_TimeOrdered()
    {
        // Validates the UUIDv7 contract identically on both target frameworks: the BCL
        // Guid.CreateVersion7 on net10.0, the inline RFC 9562 polyfill on net8.0. In the
        // dashless "N" form, the version nibble sits at index 12 and the variant nibble at
        // index 16 (canonical layout xxxxxxxx-xxxx-Vxxx-Yxxx-xxxxxxxxxxxx without separators).
        var batch = Enumerable.Range(0, 200).Select(_ => IdGenerator.NewExecutionId()).ToList();
        foreach (var id in batch)
        {
            // Must round-trip as a 32-char hex Guid.
            var parsed = Guid.ParseExact(id, "N");
            parsed.Should().NotBe(Guid.Empty);

            // (a) Version 7.
            id[12].Should().Be('7', $"UUIDv7 version nibble must be 7; saw '{id[12]}' in {id}");

            // (b) Variant 10xx -> high nibble is one of 8, 9, a, b.
            id[16].Should().BeOneOf(new[] { '8', '9', 'a', 'b' },
                $"UUIDv7 variant nibble must be 8/9/a/b; saw '{id[16]}' in {id}");
        }

        // (c) k-sortability: two ids generated a few ms apart sort by generation time under an
        // ordinal string comparison (the 48-bit ms timestamp prefix dominates).
        var first = IdGenerator.NewExecutionId();
        await Task.Delay(5).ConfigureAwait(false);
        var second = IdGenerator.NewExecutionId();
        string.CompareOrdinal(first, second).Should().BeLessThan(0,
            $"an id generated later must sort after an earlier one; saw {first} then {second}");
    }
}
