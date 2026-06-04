using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using UKBatch;
using Xunit;

namespace UKBatch.Core.Tests.Foundation;

/// <summary>
/// gate. Verifies that
/// <see cref="UKBatchOptions.ApprovalRoleClaimTypes"/> binds from <c>IConfiguration</c>. This is
/// the regression lock that fails if anyone reverts the field back to <c>IReadOnlyList&lt;string&gt;</c>
/// (which the ConfigurationBinder silently SKIPS, leaving the default in place).
/// </summary>
public class UKBatchOptionsBindingTests
{
    [Fact]
    public void ConfigurationBinding_RoleClaimTypes_FromAppSettings_PopulatesList()
    {
        // real IConfiguration.AddJsonStream → Bind(opts), NOT programmatic dict.
        // The binder MUST populate the new field from JSON array. ConfigurationBinder APPENDS
        // bound values to an existing List<T> (it does not clear) — operators that need
        // exclusive list set the default-suppressing posture via `opts.ApprovalRoleClaimTypes.Clear()`
        // BEFORE Bind. The test asserts that bound entries surface in the final list (
        // contract: List<T> shape allows binding, IReadOnlyList<T> would silently SKIP).
        const string json = @"{ ""UKBatch"": { ""ApprovalRoleClaimTypes"": [""role"", ""roles""] } }";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var config = new ConfigurationBuilder().AddJsonStream(stream).Build();

        var opts = new UKBatchOptions();
        config.GetSection("UKBatch").Bind(opts);

        // Critical assertion: the bound values appear in the list (proves binding works).
        opts.ApprovalRoleClaimTypes.Should().Contain("role",
 "ConfigurationBinder requires List<T> (not IReadOnlyList<T>) to populate.");
        opts.ApprovalRoleClaimTypes.Should().Contain("roles",
 "both JSON array entries must surface in the bound list.");
    }

    [Fact]
    public void ConfigurationBinding_RoleClaimTypes_ClearThenBind_ReplacesDefault()
    {
        // Operator-grade recipe: explicit Clear before Bind yields exclusive list semantics.
        const string json = @"{ ""UKBatch"": { ""ApprovalRoleClaimTypes"": [""role"", ""roles""] } }";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var config = new ConfigurationBuilder().AddJsonStream(stream).Build();

        var opts = new UKBatchOptions();
        opts.ApprovalRoleClaimTypes.Clear();   // shed the default so Bind() yields exclusive list.
        config.GetSection("UKBatch").Bind(opts);

        opts.ApprovalRoleClaimTypes.Should().BeEquivalentTo(new[] { "role", "roles" });
    }

    [Fact]
    public void ConfigurationBinding_RoleClaimTypes_MissingFromAppSettings_PreservesDefault()
    {
        // No ApprovalRoleClaimTypes in JSON → default ([ClaimTypes.Role]) survives.
        const string json = @"{ ""UKBatch"": { ""MaxDegreeOfParallelism"": 4 } }";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var config = new ConfigurationBuilder().AddJsonStream(stream).Build();

        var opts = new UKBatchOptions();
        config.GetSection("UKBatch").Bind(opts);

        opts.ApprovalRoleClaimTypes.Should().ContainSingle("default preserved when section missing.");
        opts.MaxDegreeOfParallelism.Should().Be(4, "other options still bind.");
    }
}
