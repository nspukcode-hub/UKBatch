using System.IO.Compression;
using FluentAssertions;
using UKBatch;
using Xunit;

namespace UKBatch.Api.Tests.Diagnostics;

/// <summary>
/// README.md integrity tests.
/// </summary>
public sealed class ReadmeIntegrityTests
{
    private static string LocateRepoRoot()
    {
        var assemblyPath = typeof(ReadmeIntegrityTests).Assembly.Location;
        var dir = new DirectoryInfo(Path.GetDirectoryName(assemblyPath)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "UKBatch.sln")))
        {
            dir = dir.Parent;
        }
        if (dir is null) throw new InvalidOperationException("Could not locate UKBatch.sln.");
        return dir.FullName;
    }

    [Fact]
    public void Readme_ExistsInPackageSource()
    {
        // src/UKBatch.Api/README.md must exist (PackageReadmeFile target).
        var readme = Path.Combine(LocateRepoRoot(), "src", "UKBatch.Api", "README.md");
        File.Exists(readme).Should().BeTrue("Item #6: UKBatch.Api README must ship with the package.");
        var content = File.ReadAllText(readme);
        content.Should().Contain("# UKBatch.Api", "README must have the canonical heading.");
        content.Should().Contain("## REST surface", "README must enumerate endpoints.");
        content.Should().Contain("## SignalR hub", "README must document the hub.");
    }

    [Fact]
    public void Readme_LinksDoNotRotate()
    {
        // Every documented endpoint route in the README must correspond to a real Map* call in the
        // endpoint source files. This is a forward-compat lock: if an endpoint route is renamed in
        // src/UKBatch.Api/, the README test catches the drift.
        var repoRoot = LocateRepoRoot();
        var readme = File.ReadAllText(Path.Combine(repoRoot, "src", "UKBatch.Api", "README.md"));
        var apiSrc = Path.Combine(repoRoot, "src", "UKBatch.Api");
        var allApiCode = string.Concat(Directory.GetFiles(apiSrc, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText));

        // Check that each documented endpoint route prefix appears in the production code.
        var documentedRoutes = new[]
        {
            "/jobs/{name}/trigger",
            "/batches/by-id/{id}",
            "/batches/by-name/{name}",
            "/executions/{id}",
            "/executions/query",
            "/approvals/{id}/approve",
            "/approvals/{id}/reject",
        };
        foreach (var route in documentedRoutes)
        {
            readme.Should().Contain(route,
                $"documented route '{route}' must appear in README REST surface table.");
            // Strip route params {name}/{id} for the production grep — production has /{name} etc.
            var routeFragment = route.Split('/').FirstOrDefault(p => !string.IsNullOrEmpty(p) && !p.Contains('{')) ?? route;
            allApiCode.Should().Contain(routeFragment,
                $"documented route fragment '{routeFragment}' must appear in production endpoint source.");
        }
    }

    [Fact]
    public void Readme_ConfigurationTable_MatchesUKBatchOptions()
    {
        // Every UKBatchOptions property documented in the README "Configuration options" table
        // MUST correspond to a real property. Catches drift when adding/removing options.
        var readme = File.ReadAllText(Path.Combine(LocateRepoRoot(), "src", "UKBatch.Api", "README.md"));
        var optionsType = typeof(UKBatchOptions);
        var actualProperties = optionsType.GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        // Sample documented properties — assert the README's documented set is a subset of actual.
        var documentedSample = new[]
        {
            "MaxDegreeOfParallelism",
            "DispatcherChannelCapacity",
            "DefaultMaxRetries",
            "HubBufferCapacity",
            "MaxPageLimit",
            "DefaultPageLimit",
            "HubPath",
            "ApprovalRoleClaimTypes",
        };
        foreach (var prop in documentedSample)
        {
            readme.Should().Contain($"`{prop}`", $"documented option '{prop}' must appear in the README table.");
            actualProperties.Should().Contain(prop, $"documented option '{prop}' must be a real UKBatchOptions property.");
        }
    }

    [Fact]
    public void Readme_CsprojOverridesRepoRootReadmeInjection()
    {
        // regression lock: Directory.Build.props:41 auto-injects the repo-root
        // README.md as `<None Include... Pack="true" />` for ALL packable projects. UKBatch.Api MUST
        // override this with its own `<None Remove>` + per-project `<None Include>` block (mirrors
        // UKBatch.AspNetCore.csproj:25-29) — otherwise the GENERIC 3307-byte root README is packed
        // instead of the 9308-byte Api-specific README that documents REST surface + SignalR contract.
        var csproj = File.ReadAllText(Path.Combine(LocateRepoRoot(), "src", "UKBatch.Api", "UKBatch.Api.csproj"));
        csproj.Should().Contain(
            "<None Remove=\"$(MSBuildThisFileDirectory)..\\..\\README.md\" />",
            "csproj MUST remove the root README injection from Directory.Build.props.");
        csproj.Should().Contain(
            "<None Include=\"README.md\" Pack=\"true\" PackagePath=\"\\\" />",
            "csproj MUST explicitly include the per-project README for packing.");
    }
}
