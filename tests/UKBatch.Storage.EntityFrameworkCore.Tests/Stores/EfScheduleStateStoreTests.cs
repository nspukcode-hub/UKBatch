using FluentAssertions;
using UKBatch.Storage.EntityFrameworkCore.Stores;
using UKBatch.Storage.EntityFrameworkCore.Tests.Infrastructure;
using Xunit;

namespace UKBatch.Storage.EntityFrameworkCore.Tests.Stores;

/// <summary>
/// <see cref="EfScheduleStateStore"/> behavior on SQLite: a watermark round-trips through GetAll, an
/// absent definition is inserted, the store is <b>monotonic</b> (an older occurrence never regresses a
/// newer one), and multiple definitions are kept independent.
/// </summary>
public sealed class EfScheduleStateStoreTests : IAsyncLifetime
{
    private SqliteStoreHarness _harness = default!;
    private EfScheduleStateStore _store = default!;

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        _harness = await SqliteStoreHarness.CreateAsync();
        _store = new EfScheduleStateStore(_harness.Factory);
    }

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    public async Task GetAllAsync_Empty_ReturnsEmpty()
    {
        var all = await _store.GetAllAsync(CancellationToken.None);
        all.Should().BeEmpty();
    }

    [Fact]
    public async Task RecordFiredAsync_AbsentDefinition_Inserts_AndRoundTrips()
    {
        await _store.RecordFiredAsync("def-1", T0, CancellationToken.None);

        var all = await _store.GetAllAsync(CancellationToken.None);
        all.Should().ContainKey("def-1");
        all["def-1"].Should().Be(T0);
    }

    [Fact]
    public async Task RecordFiredAsync_NewerThanStored_Advances()
    {
        await _store.RecordFiredAsync("def-1", T0, CancellationToken.None);
        await _store.RecordFiredAsync("def-1", T0.AddHours(1), CancellationToken.None);

        var all = await _store.GetAllAsync(CancellationToken.None);
        all["def-1"].Should().Be(T0.AddHours(1), "a newer occurrence advances the watermark");
    }

    [Fact]
    public async Task RecordFiredAsync_OlderThanStored_IsNoOp_StaysNewer()
    {
        await _store.RecordFiredAsync("def-1", T0.AddHours(1), CancellationToken.None);
        await _store.RecordFiredAsync("def-1", T0, CancellationToken.None);   // older — must NOT regress

        var all = await _store.GetAllAsync(CancellationToken.None);
        all["def-1"].Should().Be(T0.AddHours(1),
            "the store is monotonic — an older write cannot regress the watermark to the past");
    }

    [Fact]
    public async Task RecordFiredAsync_EqualToStored_IsNoOp()
    {
        await _store.RecordFiredAsync("def-1", T0, CancellationToken.None);
        await _store.RecordFiredAsync("def-1", T0, CancellationToken.None);   // not strictly newer

        var all = await _store.GetAllAsync(CancellationToken.None);
        all["def-1"].Should().Be(T0);
    }

    [Fact]
    public async Task RecordFiredAsync_MultipleDefinitions_KeptIndependent()
    {
        await _store.RecordFiredAsync("def-1", T0, CancellationToken.None);
        await _store.RecordFiredAsync("def-2", T0.AddDays(1), CancellationToken.None);

        var all = await _store.GetAllAsync(CancellationToken.None);
        all.Should().HaveCount(2);
        all["def-1"].Should().Be(T0);
        all["def-2"].Should().Be(T0.AddDays(1));
    }
}
