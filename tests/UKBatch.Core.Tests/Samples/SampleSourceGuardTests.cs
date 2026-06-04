using FluentAssertions;
using Xunit;

namespace UKBatch.Core.Tests.Samples;

/// <summary>
/// SOURCE GUARD against re-introduction of reflection into
/// <c>samples/Sample.BatchWorkflow/Program.cs</c>. The original reflection block was replaced with a
/// short <see cref="UKBatch.Abstractions.Batches.IBatchDefinitionLookup"/> call; this test reads
/// the source file as text and asserts the stable grep substring
/// <c>"BatchDefinitionRegistry, UKBatch.Core"</c> does NOT appear.
/// </summary>
/// <remarks>
/// Source-file location strategy: walk UP from <see cref="AppContext.BaseDirectory"/> until a
/// directory containing <c>UKBatch.sln</c> is found, then join
/// <c>samples/Sample.BatchWorkflow/Program.cs</c>. Skip with a clear reason if the walk fails
/// (e.g. packaged-test-bundle CI layouts that ship only test binaries — keeps green CI without
/// suppressing the regression net in the normal developer / GitHub Actions flow).
/// </remarks>
public sealed class SampleSourceGuardTests
{
    private const string GrepTarget = "BatchDefinitionRegistry, UKBatch.Core";
    private const string SampleRelativePath = "samples/Sample.BatchWorkflow/Program.cs";

    [Fact]
    public void Sample_BatchWorkflow_Program_DoesNotReferenceBatchDefinitionRegistryReflection()
    {
        var solutionRoot = FindSolutionRoot(AppContext.BaseDirectory);
        if (solutionRoot is null)
        {
            // Skip cleanly when source isn't on disk (packaged CI layout). Use Skip.If pattern
            // via Assert.Skip from xUnit v3; fall back to early-return + comment for v2.
            // Project uses xunit v2 per the test SDK; emit a clear message and return.
            // The runtime behavior test still catches regressions; this guard is a build-time policy.
            Assert.Fail(
                "Sample source not available in this test layout — could not locate UKBatch.sln " +
                $"by walking up from '{AppContext.BaseDirectory}'. If this is a packaged-test " +
                "bundle CI run, mark the test as skipped instead of failing.");
            return;
        }

        var samplePath = Path.Combine(solutionRoot, SampleRelativePath);
        File.Exists(samplePath).Should().BeTrue(
            $"sample source must exist at expected path '{samplePath}'");

        var source = File.ReadAllText(samplePath);
        Assert.DoesNotContain(GrepTarget, source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Walks the directory tree upward from <paramref name="start"/> looking for a folder
    /// containing <c>UKBatch.sln</c>. Returns the matching directory, or <c>null</c> if the
    /// walk reaches the filesystem root without finding the solution.
    /// </summary>
    private static string? FindSolutionRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "UKBatch.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
