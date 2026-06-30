using System.Collections.Concurrent;
using FluentAssertions;
using UKBatch.Abstractions.Jobs;
using Xunit;

namespace UKBatch.Core.Tests.Foundation;

/// <summary>
/// Pins <see cref="JobOutputs"/>: the thread-safe sink a job writes its forwarded output values into.
/// <see cref="JobOutputs.Set"/> is last-writer-wins and the single synchronized door (a single instance is
/// shared across the N partition workers of a partitioned job); <see cref="JobOutputs.Snapshot"/> returns an
/// independent copy; and concurrent writes from many workers never lose a key.
/// </summary>
public class JobOutputsTests
{
    [Fact]
    public void NewlyCreated_IsEmpty()
    {
        new JobOutputs().IsEmpty.Should().BeTrue("a job that writes nothing leaves the sink empty");
    }

    [Fact]
    public void Set_MakesNotEmpty()
    {
        var outputs = new JobOutputs();
        outputs.Set("k", 1);

        outputs.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void Set_RecordsValue_InSnapshot()
    {
        var outputs = new JobOutputs();
        outputs.Set("orderId", 5);

        outputs.Snapshot().Should().ContainKey("orderId").WhoseValue.Should().Be(5);
    }

    [Fact]
    public void Set_RepeatedKey_LastWriteWins()
    {
        var outputs = new JobOutputs();
        outputs.Set("k", 1);
        outputs.Set("k", 2);
        outputs.Set("k", 3);

        outputs.Snapshot()["k"].Should().Be(3, "the most recent write for a key wins");
    }

    [Fact]
    public void Set_NullValue_IsAllowed_AndKeyPresent()
    {
        var outputs = new JobOutputs();
        outputs.Set("k", null);

        var snapshot = outputs.Snapshot();
        snapshot.Should().ContainKey("k");
        snapshot["k"].Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Set_NullOrEmptyKey_Throws(string? key)
    {
        var act = () => new JobOutputs().Set(key!, 1);

        act.Should().Throw<ArgumentException>("a key is required to address an output value");
    }

    [Fact]
    public void Snapshot_IsIndependentCopy_LaterSetsDoNotMutateIt()
    {
        var outputs = new JobOutputs();
        outputs.Set("a", 1);

        var snapshot = outputs.Snapshot();
        outputs.Set("b", 2);

        snapshot.Should().ContainKey("a");
        snapshot.Should().NotContainKey("b", "a snapshot is a point-in-time copy, not a live view");
    }

    [Fact]
    public void Snapshot_TwoCalls_AreDistinctInstances()
    {
        var outputs = new JobOutputs();
        outputs.Set("a", 1);

        outputs.Snapshot().Should().NotBeSameAs(outputs.Snapshot());
    }

    [Fact]
    public async Task Set_ConcurrentWritesFromManyWorkers_NoLostKeys()
    {
        // The sink is shared across the N partition workers of a partitioned job, so concurrent Set calls
        // must all land. Many parallel tasks each write a distinct key; the final snapshot must contain
        // every one of them (no lost writes under contention).
        const int writers = 256;
        var outputs = new JobOutputs();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, writers),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            (i, _) =>
            {
                outputs.Set($"key-{i}", i);
                return ValueTask.CompletedTask;
            });

        var snapshot = outputs.Snapshot();
        snapshot.Should().HaveCount(writers, "every concurrent write must be retained");
        for (var i = 0; i < writers; i++)
        {
            snapshot[$"key-{i}"].Should().Be(i);
        }
    }

    [Fact]
    public async Task Set_ConcurrentWritesToSameKey_ResolvesToOneOfTheValues()
    {
        // Concurrent last-writer-wins for a single key: many writers race on the same key; the final value
        // must be one of the values written (no corruption / torn write), and the key is present exactly once.
        const int writers = 200;
        var outputs = new JobOutputs();
        var written = new ConcurrentBag<int>();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, writers),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            (i, _) =>
            {
                outputs.Set("contended", i);
                written.Add(i);
                return ValueTask.CompletedTask;
            });

        var snapshot = outputs.Snapshot();
        snapshot.Should().ContainKey("contended");
        var final = (int)snapshot["contended"]!;
        written.Should().Contain(final, "the surviving value is one of the concurrently written values");
    }
}
