using System.Reflection;
using FluentAssertions;
using UKBatch.Dashboard.Clients;
using Xunit;

namespace UKBatch.Dashboard.Tests.Diagnostics;

/// <summary>
/// Dashboard package invariants:
/// <list type="bullet">
/// <item><see cref="IUKBatchClient"/> surface lock (2 props + 5 events + 29 methods = 36 reflection members).</item>
/// <item>Friend-access discipline — the Dashboard consumes a single allowed Core internal type (lock test mirrors the Api side).</item>
/// </list>
/// </summary>
public sealed class DashboardPackageInvariants
{
    [Fact]
    public void IUKBatchClient_HasExactly_2_Properties()
    {
        var t = typeof(IUKBatchClient);
        var props = t.GetProperties(BindingFlags.Instance | BindingFlags.Public);
        props.Select(p => p.Name).Should().BeEquivalentTo("Service", "State");
    }

    [Fact]
    public void IUKBatchClient_HasExactly_5_Events()
    {
        // StateChanged + 4 hub events (ExecutionStateChanged, ProgressUpdated,
        // ApprovalRequested, BatchCompleted).
        var t = typeof(IUKBatchClient);
        var events = t.GetEvents(BindingFlags.Instance | BindingFlags.Public);
        events.Select(e => e.Name).Should().BeEquivalentTo(
            "StateChanged",
            "ExecutionStateChanged",
            "ProgressUpdated",
            "ApprovalRequested",
            "BatchCompleted");
    }

    [Fact]
    public void IUKBatchClient_HasExactly_29_PublicMethods()
    {
        // Lifecycle: ConnectAsync, DisconnectAsync (2)
        // REST jobs: ListJobsAsync, GetJobAsync, TriggerJobAsync (3)
        // REST batches: ListBatchesAsync, GetBatchByIdAsync, GetBatchByNameAsync, RunBatchByIdAsync,
        // GetBatchRunStatusAsync (5) + CreateBatchAsync, UpdateBatchAsync, DeleteBatchAsync (3) = 8
        // REST executions: GetExecutionAsync, QueryExecutionsAsync, CancelExecutionAsync (3)
        // REST approvals: ListApprovalsAsync, ApproveAsync, RejectAsync, ListBatchGatesAsync (4)
        // REST workers: GetWorkersAsync (1)
        // Hub subs: 8 (Subscribe/Unsubscribe × {Execution, Batch, Job, All})
        var t = typeof(IUKBatchClient);
        // Exclude property accessors + event add/remove + DisposeAsync (inherited from IAsyncDisposable).
        var methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => !m.IsSpecialName)
            .Where(m => m.DeclaringType == typeof(IUKBatchClient))
            .ToArray();
        methods.Should().HaveCount(29,
            "IUKBatchClient surface lock: 2 lifecycle + 19 REST (incl. CreateBatchAsync, UpdateBatchAsync, DeleteBatchAsync, ListBatchGatesAsync, GetWorkersAsync) + 8 hub subs = 29 methods. " +
            $"Actual: {string.Join(", ", methods.Select(m => m.Name))}");
        methods.Select(m => m.Name).Should().Contain("GetWorkersAsync",
            "the dashboard Workers panel relies on the GetWorkersAsync REST surface.");
        methods.Select(m => m.Name).Should().Contain("ListBatchGatesAsync",
            "the run-detail gate colouring reads every gate's own outcome via ListBatchGatesAsync.");
    }

    [Fact]
    public void IUKBatchClient_TotalSurface_Is_36_Members()
    {
        // Combined: 2 properties + 5 events + 29 methods = 36 reflection members.
        var t = typeof(IUKBatchClient);
        var propCount = t.GetProperties(BindingFlags.Instance | BindingFlags.Public).Length;
        var eventCount = t.GetEvents(BindingFlags.Instance | BindingFlags.Public).Length;
        var methodCount = t.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => !m.IsSpecialName)
            .Where(m => m.DeclaringType == typeof(IUKBatchClient))
            .Count();
        (propCount + eventCount + methodCount).Should().Be(36,
            "IUKBatchClient surface: 2 props + 5 events + 29 methods = 36 members.");
    }

    [Fact]
    public void Api_FriendAccess_LimitedToSevenCoreInternalTypes()
    {
        // Friend-access governance: across Api + Dashboard + Transport.Http, the only Core
        // internal types granted InternalsVisibleTo are BatchStateMachine, IApprovalGateEvents,
        // IProgressBeatBroadcaster, IBatchCompletionEvents, BatchCompletionSignalPayload and
        // LruDedupeCache<TKey>.
        //
        // Mirror of UKBatch.Api.Tests.Diagnostics.ApiPackageInvariantsTests. This Dashboard-side
        // instance verifies the same source-grep on the UKBatch.Dashboard project tree — only
        // LruDedupeCache should appear as the sole Core-internal reference.
        var dashboardDir = Path.Combine(LocateRepoRoot(), "src", "UKBatch.Dashboard");
        var forbiddenInternalTypeReferences = new[]
        {
            "JobDispatcher.",
            "JobWorker.",
            "BatchExecutor.",
            "JobScheduler.",
            "ExponentialRetryPolicy",
            "ApprovalGateService.",
            "DebouncedProgressFlusher.",
            "CountingJobProgress.",
            "IdGenerator.",
            "JobLoggerFactory.",
            "JobRunner.",
            "InMemoryJobStore.",
            "BatchDefinitionRegistry.",
            "JobDefinitionRegistry.",
            "BatchStateMachine.",
            "ApprovalGateEvents.",
            "ProgressBeatBroadcaster.",
            "BatchCompletionEvents.",
            "BatchCompletionSignalPayload.",
        };
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(dashboardDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }
            var text = File.ReadAllText(file);
            foreach (var bad in forbiddenInternalTypeReferences)
            {
                var idx = 0;
                while ((idx = text.IndexOf(bad, idx, StringComparison.Ordinal)) >= 0)
                {
                    var prevChar = idx == 0 ? ' ' : text[idx - 1];
                    var isIdentifierContinuation = char.IsLetterOrDigit(prevChar) || prevChar == '_';
                    if (!isIdentifierContinuation)
                    {
                        offenders.Add($"{file}: forbidden internal reference '{bad}'");
                        break;
                    }
                    idx += bad.Length;
                }
            }
        }
        offenders.Should().BeEmpty(
            "friend-access discipline: UKBatch.Dashboard consumes only LruDedupeCache<TKey> from Core internals.");
    }

    private static string LocateRepoRoot()
    {
        var assemblyPath = typeof(DashboardPackageInvariants).Assembly.Location;
        var dir = new DirectoryInfo(Path.GetDirectoryName(assemblyPath)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "UKBatch.sln")))
        {
            dir = dir.Parent;
        }
        if (dir is null) throw new InvalidOperationException("Could not locate UKBatch.sln in any parent directory.");
        return dir.FullName;
    }
}
