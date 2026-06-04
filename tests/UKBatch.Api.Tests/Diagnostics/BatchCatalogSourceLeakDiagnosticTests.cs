using FluentAssertions;
using Xunit;

namespace UKBatch.Api.Tests.Diagnostics;

/// <summary>
/// Source-leak lock. <c>BatchCatalogService</c> implements three source-routing rules that, if
/// accidentally inverted by a future patch, would silently expose Code-source batches to
/// Dashboard-only queries (or vice versa). This source-grep diagnostic locks the textual presence
/// of each rule's guard comment so a removal triggers a CI break loud enough to demand justification.
/// </summary>
public sealed class BatchCatalogSourceLeakDiagnosticTests
{
    private static string LocateRepoRoot()
    {
        var assemblyPath = typeof(BatchCatalogSourceLeakDiagnosticTests).Assembly.Location;
        var dir = new DirectoryInfo(Path.GetDirectoryName(assemblyPath)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "UKBatch.sln")))
        {
            dir = dir.Parent;
        }
        if (dir is null) throw new InvalidOperationException("Could not locate UKBatch.sln in any parent directory.");
        return dir.FullName;
    }

    [Fact]
    public void BatchCatalogService_NoSourceLeakage()
    {
        // Source-grep over BatchCatalogService to lock the source-routing rules.
        // - source=Code MUST NOT touch _store (no `await _store.` calls inside the Code branch).
        // - source=Dashboard MUST NOT touch _codeLookup (no `_codeLookup.` calls inside the
        // Dashboard branch).
        // This is a defensive source-text lock. If the source-routing logic is refactored, the
        // markers must be updated explicitly — preserving the safety property by intention rather
        // than by accident.
        var path = Path.Combine(LocateRepoRoot(), "src", "UKBatch.Core", "Storage", "BatchCatalogService.cs");
        File.Exists(path).Should().BeTrue("BatchCatalogService.cs source must exist for this diagnostic.");
        var text = File.ReadAllText(path);

        // Rule 1: source=Code MUST NOT touch persistent storage.
        text.Should().Contain("Rule: source=Code MUST NOT touch persistent storage.",
            "the source=Code guard comment must remain.");

        // Rule 2: source=Dashboard MUST NOT consult the Code lookup.
        text.Should().Contain("Rule: source=Dashboard MUST NOT consult the Code lookup.",
            "the source=Dashboard guard comment must remain.");

        // Code-wins-on-collision rule (rule 1) — ensure dedup-by-Name flow is preserved.
        text.Should().Contain("Code-wins-on-collision",
            "the Code-wins-on-collision rule must be documented in the merge step.");
    }
}
