using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using UKBatch.Storage;          // InMemoryJobStore lives in UKBatch.Core (namespace UKBatch.Storage)
using Xunit;

namespace UKBatch.Storage.EntityFrameworkCore.Tests.Diagnostics;

/// <summary>
/// The real friend-access guard. The <c>Api_FriendAccess_LimitedToSeven*</c> tests are
/// SOURCE-GREPS of <c>src/UKBatch.Api</c> + <c>src/UKBatch.Dashboard</c> only — they do NOT see
/// this adapter and do NOT reflect on the Core assembly's InternalsVisibleTo grant. This test
/// locks the assembly-level grant so the EF adapter (and every future Redis/RabbitMQ adapter)
/// CANNOT silently become an additional friend: the decision to promote cross-store contracts to
/// Abstractions-public (never grant friend access to adapters) is enforced here, not assumed.
/// </summary>
public sealed class EfFriendAccessInvariantTests
{
    // The four sanctioned friends, verified against Core.csproj.
    private static readonly string[] SanctionedFriends =
    {
        "UKBatch.Core.Tests",
        "UKBatch.Api",
        "UKBatch.Dashboard",
        "UKBatch.Transport.Http",
    };

    private static IReadOnlyList<string> CoreGrantSet()
    {
        // InMemoryJobStore is public-in-Core (`public sealed`, namespace UKBatch.Storage) so it resolves
        // without a friend grant and anchors typeof(...).Assembly == UKBatch.Core.
        var coreAssembly = typeof(InMemoryJobStore).Assembly;
        coreAssembly.GetName().Name.Should().Be("UKBatch.Core", "anchor type must live in Core");
        return coreAssembly.GetCustomAttributes<InternalsVisibleToAttribute>()
            // AssemblyName can carry a PublicKey suffix ("Asm, PublicKey=..."); take the bare name.
            .Select(a => a.AssemblyName.Split(',')[0].Trim())
            .ToList();
    }

    [Fact]
    public void Core_InternalsVisibleTo_GrantSet_DoesNotIncludeEfAdapter()
    {
        CoreGrantSet().Should().NotContain(
            "UKBatch.Storage.EntityFrameworkCore",
            "the EF adapter consumes Abstractions-public contracts (IJobStoreInternal, "
            + "JobStatusTransitions, IJobExecutionWatchHub, IApprovalGateStore) — it must NEVER be "
            + "granted friend access to Core internals (that path does not scale to Redis/RabbitMQ).");
    }

    [Fact]
    public void Core_InternalsVisibleTo_GrantSet_IsExactlyTheFourSanctionedFriends()
    {
        CoreGrantSet().Should().BeEquivalentTo(SanctionedFriends,
            "the governance threshold caps Core friend assemblies at these four; "
            + "adding a fifth requires a deliberate review, not a quiet csproj edit.");
    }

    [Fact]
    public void Core_InternalsVisibleTo_GrantSet_HasExactlyFourEntries()
    {
        // Sanity-confirm the assertion logic: if a fifth InternalsVisibleTo (e.g. the EF adapter) were
        // added to Core.csproj, the count would change and the BeEquivalentTo above would fail. This
        // explicit count lock makes that failure mode unambiguous.
        CoreGrantSet().Should().HaveCount(4,
            "exactly four friend assemblies are sanctioned; a fifth grant would break the contract.");
    }
}
