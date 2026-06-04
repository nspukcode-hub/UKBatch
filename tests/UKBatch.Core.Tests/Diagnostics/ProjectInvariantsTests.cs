using System.Diagnostics;
using FluentAssertions;
using Xunit;

namespace UKBatch.Core.Tests.Diagnostics;

/// <summary>
/// CI grep gate. Ensures no executable `.GetAwaiter().GetResult()` hits in <c>src/UKBatch.Core</c>.
/// The runtime must be async-all-the-way.
/// </summary>
public class ProjectInvariantsTests
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

    [Fact]
    public void Core_NoSyncOverAsync_GetAwaiterGetResult()
    {
        var root = LocateRepoRoot();
        var corePath = Path.Combine(root, "src", "UKBatch.Core");

        // We scan source files line-by-line. xmldoc / line-comments containing the literal in a
        // backtick code-quote ("no `.GetAwaiter().GetResult()`") are NOT violations; only
        // EXECUTABLE sync-over-async hits are forbidden by this invariant.
        var offenders = new List<(string File, int Line, string Text)>();
        foreach (var file in Directory.EnumerateFiles(corePath, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!line.Contains(".GetAwaiter().GetResult()", StringComparison.Ordinal))
                {
                    continue;
                }
                // Skip xmldoc + line-comment lines — those are documentation references, not code.
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
                    trimmed.StartsWith("*", StringComparison.Ordinal))
                {
                    continue;
                }
                offenders.Add((file, i + 1, line));
            }
        }
        offenders.Should().BeEmpty(
            "no executable sync-over-async; the runtime must be async-all-the-way.");
    }

    [Fact]
    public void Core_BuildsClean_UnderTreatWarningsAsErrors()
    {
        // Smoke check — UKBatch.Core builds without warnings under TreatWarningsAsErrors=true.
        var root = LocateRepoRoot();
        var corePath = Path.Combine(root, "src", "UKBatch.Core", "UKBatch.Core.csproj");

        var psi = new ProcessStartInfo("dotnet", $"build \"{corePath}\" -c Release --nologo")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = root,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);

        p.ExitCode.Should().Be(0, $"UKBatch.Core must build clean under TreatWarningsAsErrors. STDOUT:\n{output}\nSTDERR:\n{error}");
    }
}
