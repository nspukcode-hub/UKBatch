using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using UKBatch.Abstractions.Workers;
using UKBatch.Api.Workers;
using Xunit;

namespace UKBatch.Api.Tests.Workers;

/// <summary>
/// <see cref="InMemoryWorkerRegistry"/> TTL + eviction + ordering behavior, driven
/// by a <see cref="FakeTimeProvider"/>. The registry takes an explicit <c>now</c> on every call, so the
/// TTL math (45s online window / 10min hard-evict) is fully deterministic. Internals are reachable via
/// <c>InternalsVisibleTo UKBatch.Api.Tests</c>.
/// </summary>
public sealed class InMemoryWorkerRegistryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static (InMemoryWorkerRegistry Registry, FakeTimeProvider Time) Build()
    {
        var time = new FakeTimeProvider(T0);
        return (new InMemoryWorkerRegistry(time), time);
    }

    private static WorkerBeatRequest Beat(string name, WorkerStatus status = WorkerStatus.Online, IReadOnlyList<string>? jobs = null, IReadOnlyList<string>? tags = null)
        => new() { Name = name, Status = status, Jobs = jobs ?? [], Tags = tags ?? [] };

    [Fact]
    public void Upsert_ThenList_FreshBeat_IsOnline()
    {
        var (registry, time) = Build();
        registry.Upsert(Beat("invoicing", jobs: ["GenerateInvoice"], tags: ["billing"]), time.GetUtcNow());

        var list = registry.List(time.GetUtcNow());

        list.Should().ContainSingle();
        var w = list[0];
        w.Name.Should().Be("invoicing");
        w.Online.Should().BeTrue("a beat at 'now' is well within the 45s online TTL");
        w.Jobs.Should().BeEquivalentTo("GenerateInvoice");
        w.Tags.Should().BeEquivalentTo("billing");
        w.LastSeenUtc.Should().Be(T0);
    }

    [Fact]
    public void List_PastOnlineTtl_IsOffline_ButStillListed()
    {
        var (registry, time) = Build();
        registry.Upsert(Beat("invoicing"), time.GetUtcNow());

        time.Advance(TimeSpan.FromSeconds(46)); // > 45s online TTL, < 10min hard-evict
        var list = registry.List(time.GetUtcNow());

        list.Should().ContainSingle("the row is retained past the online TTL so the panel can show 'last seen'");
        list[0].Online.Should().BeFalse("46s since last beat exceeds the 45s online TTL");
    }

    [Fact]
    public void List_PastHardEvictHorizon_RowIsRemoved()
    {
        var (registry, time) = Build();
        registry.Upsert(Beat("invoicing"), time.GetUtcNow());

        time.Advance(TimeSpan.FromMinutes(11)); // > 10min hard-evict
        var list = registry.List(time.GetUtcNow());

        list.Should().BeEmpty("a row older than the 10min hard-evict horizon is dropped from the list");

        // And it stays gone (lazy hard-evict mutated the store).
        registry.List(time.GetUtcNow()).Should().BeEmpty();
    }

    [Fact]
    public void List_ExplicitOfflineBeat_IsOfflineImmediately_WithinTtl()
    {
        var (registry, time) = Build();
        // An explicit Offline beat (graceful stop) at 'now' — well within the online TTL window.
        registry.Upsert(Beat("invoicing", WorkerStatus.Offline), time.GetUtcNow());

        var list = registry.List(time.GetUtcNow());

        list.Should().ContainSingle();
        list[0].Online.Should().BeFalse(
            "an explicit Offline beat flips Online=false immediately, even though LastSeenUtc is fresh");
        list[0].Status.Should().Be(WorkerStatus.Offline);
    }

    [Fact]
    public async Task Upsert_Concurrent_NoLoss_CountStable()
    {
        var (registry, time) = Build();
        var now = time.GetUtcNow();
        const int workerCount = 200;

        // N distinct workers upserted concurrently → all present, none lost.
        await Task.WhenAll(Enumerable.Range(0, workerCount).Select(i =>
            Task.Run(() => registry.Upsert(Beat($"w{i:D4}"), now))));

        var list = registry.List(now);
        list.Should().HaveCount(workerCount, "ConcurrentDictionary upserts under contention lose nothing");
        list.Select(w => w.Name).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void List_SortsOrdinalByName()
    {
        var (registry, time) = Build();
        var now = time.GetUtcNow();
        // Insert in non-sorted order; ordinal sort is case-sensitive (uppercase precedes lowercase).
        registry.Upsert(Beat("zeta"), now);
        registry.Upsert(Beat("Alpha"), now);
        registry.Upsert(Beat("beta"), now);

        var names = registry.List(now).Select(w => w.Name).ToArray();

        // List sorts ordinal by name for stable UI ordering (uppercase 'A'=65 sorts before
        // lowercase 'b'=98/'z'=122).
        names.Should().ContainInOrder("Alpha", "beta", "zeta");
        names.Should().HaveCount(3);
    }

    [Fact]
    public void Upsert_SameName_OverwritesPriorBeat()
    {
        var (registry, time) = Build();
        registry.Upsert(Beat("invoicing", jobs: ["Old"]), time.GetUtcNow());

        time.Advance(TimeSpan.FromSeconds(5));
        registry.Upsert(Beat("invoicing", jobs: ["New"]), time.GetUtcNow());

        var list = registry.List(time.GetUtcNow());
        list.Should().ContainSingle("the registry key is the worker name — a re-beat overwrites, not appends");
        list[0].Jobs.Should().BeEquivalentTo("New");
        list[0].LastSeenUtc.Should().Be(T0 + TimeSpan.FromSeconds(5), "LastSeenUtc advances to the latest beat");
    }

    [Fact]
    public void Upsert_NullBeat_Throws()
    {
        var (registry, time) = Build();
        Action act = () => registry.Upsert(null!, time.GetUtcNow());
        act.Should().Throw<ArgumentNullException>();
    }
}
