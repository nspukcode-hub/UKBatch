using FluentAssertions;
using Xunit;

namespace UKBatch.Dashboard.Tests.Diagnostics;

/// <summary>
/// Source-grep lock for the <c>Workers.razor</c> poll path. bunit cannot reliably advance the
/// production <c>PeriodicTimer</c> in real time, so the "auto-refresh polls <c>GetWorkersAsync</c>
/// on a cancellable loop and tears down cleanly" invariant is asserted at the source level
/// (mirrors the repo's existing source-grep discipline, e.g. DagCanvasAssetRegressionTests).
/// If someone deletes the poll loop or its cancellation, this fails before the regression ships.
/// </summary>
public sealed class WorkersPollSourceInvariantTests
{
    private static string WorkersRazor()
    {
        var path = Path.Combine(
            LocateRepoRoot(), "src", "UKBatch.Dashboard", "Components", "Pages", "Workers.razor");
        File.Exists(path).Should().BeTrue($"Workers.razor must exist at {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void WorkersRazor_PollLoop_CallsGetWorkersAsync()
    {
        var src = WorkersRazor();
        src.Should().Contain("PeriodicTimer",
            "the panel auto-refreshes via a PeriodicTimer (REST poll, no SignalR)");
        src.Should().Contain("GetWorkersAsync",
            "the load path (shared by the initial render and the poll loop) calls GetWorkersAsync");
        src.Should().Contain("WaitForNextTickAsync",
            "the poll loop drives refreshes off the timer tick");
    }

    [Fact]
    public void WorkersRazor_PollLoop_CancelsAndDisposesCleanly()
    {
        var src = WorkersRazor();
        // Symmetric teardown: a CTS cancels the loop, DisposeAsync awaits it.
        src.Should().Contain("CancellationTokenSource", "the poll loop is cancellable");
        src.Should().Contain("DisposeAsync", "the page implements async disposal to await the loop teardown");
        src.Should().Contain("InvokeAsync(StateHasChanged)",
            "the poll loop hops to the render dispatcher before mutating render state");
    }

    private static string LocateRepoRoot()
    {
        var assemblyPath = typeof(WorkersPollSourceInvariantTests).Assembly.Location;
        var dir = new DirectoryInfo(Path.GetDirectoryName(assemblyPath)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "UKBatch.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate UKBatch.sln in any parent directory.");
        }

        return dir.FullName;
    }
}
