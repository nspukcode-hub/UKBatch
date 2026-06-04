using FluentAssertions;
using Xunit;

namespace UKBatch.AspNetCore.Tests.Diagnostics;

/// <summary>
/// CI grep gate. Ensures no <c>.GetAwaiter().GetResult()</c> hits in
/// <c>src/UKBatch.AspNetCore</c>, <c>samples/</c>, or
/// <c>tests/UKBatch.AspNetCore.Tests</c>. The bridge package and the samples MUST be
/// async-all-the-way.
/// </summary>
public sealed class AspNetCoreProjectInvariantsTests
{
    private static string LocateRepoRoot()
    {
        // Walk up from the test binary until we find UKBatch.sln.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "UKBatch.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("UKBatch.sln not found by walking up from base directory");
    }

    private static IEnumerable<string> EnumerateCsFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }
            yield return file;
        }
    }

    [Fact]
    public void NoSyncOverAsync_GetAwaiterGetResult()
    {
        var root = LocateRepoRoot();
        var roots = new[]
        {
            Path.Combine(root, "src", "UKBatch.AspNetCore"),
            Path.Combine(root, "samples"),
            Path.Combine(root, "tests", "UKBatch.AspNetCore.Tests"),
        };

        var offenders = new List<(string File, int Line, string Text)>();
        // Exclude this test file itself — it contains the forbidden literal as a string match target.
        var selfPath = Path.Combine(root, "tests", "UKBatch.AspNetCore.Tests", "Diagnostics", "AspNetCoreProjectInvariantsTests.cs");
        // The forbidden literal — broken into pieces to avoid self-matching.
        var forbidden = "." + "GetAwaiter()." + "GetResult()";
        foreach (var dir in roots)
        {
            foreach (var file in EnumerateCsFiles(dir))
            {
                if (string.Equals(file, selfPath, StringComparison.Ordinal))
                {
                    continue;
                }
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (!line.Contains(forbidden, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    // Skip xmldoc / line-comments.
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
                        (trimmed.Length > 0 && trimmed[0] == '*'))
                    {
                        continue;
                    }
                    offenders.Add((file, i + 1, line));
                }
            }
        }
        offenders.Should().BeEmpty(
            "no executable sync-over-async across UKBatch.AspNetCore + samples + tests.");
    }

    /// <summary>
    /// Negative test — the bridge package must not add public types to UKBatch.Abstractions. Smoke check.
    /// </summary>
    [Fact]
    public void NoPublicTypesAddedToAbstractions()
    {
        // We assert against the published Abstractions assembly. If the bridge package grew public
        // types in Abstractions, this fails.
        var abstractionsAsm = typeof(UKBatch.Abstractions.Jobs.IJob).Assembly;
        var publicTypes = abstractionsAsm.GetExportedTypes();
        // The web-bridge surface lives in UKBatch.AspNetCore; we just ensure no bridge-only types
        // snuck into Abstractions.
        var forbiddenPrefixes = new[]
        {
            "UKBatch.Abstractions.AspNetCore",
            "UKBatch.Abstractions.Triggering",
            "UKBatch.Abstractions.HealthChecks",
            "UKBatch.Abstractions.Tracing",
        };
        var offenders = publicTypes
            .Where(t => forbiddenPrefixes.Any(p => t.FullName?.StartsWith(p, StringComparison.Ordinal) == true))
            .Select(t => t.FullName!)
            .ToList();
        offenders.Should().BeEmpty(
            "the bridge package adds no public types to UKBatch.Abstractions (the bridge surface lives in UKBatch.AspNetCore).");
    }
}
