using System.Reflection;
using FluentAssertions;
using UKBatch.Dashboard.Clients;
using Xunit;

namespace UKBatch.Dashboard.Tests.Diagnostics;

/// <summary>
/// Dashboard package invariants:
/// <list type="bullet">
/// <item><see cref="IUKBatchClient"/> surface lock (2 props + 5 events + 31 methods = 38 reflection members).</item>
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
    public void IUKBatchClient_HasExactly_33_PublicMethods()
    {
        // Lifecycle: ConnectAsync, DisconnectAsync (2)
        // REST jobs: ListJobsAsync, GetJobAsync, TriggerJobAsync (3)
        // REST batches: ListBatchesAsync, GetBatchByIdAsync, GetBatchByNameAsync, RunBatchByIdAsync,
        // GetBatchRunStatusAsync (5) + CreateBatchAsync, UpdateBatchAsync, DeleteBatchAsync (3)
        // + QueryRunsAsync, CancelRunAsync, RetryRunAsync (3) + SetScheduleEnabledAsync (1) = 12
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
        methods.Should().HaveCount(33,
            "IUKBatchClient surface lock: 2 lifecycle + 23 REST (incl. CreateBatchAsync, UpdateBatchAsync, DeleteBatchAsync, QueryRunsAsync, CancelRunAsync, RetryRunAsync, SetScheduleEnabledAsync, ListBatchGatesAsync, GetWorkersAsync) + 8 hub subs = 33 methods. " +
            $"Actual: {string.Join(", ", methods.Select(m => m.Name))}");
        methods.Select(m => m.Name).Should().Contain("GetWorkersAsync",
            "the dashboard Workers panel relies on the GetWorkersAsync REST surface.");
        methods.Select(m => m.Name).Should().Contain("ListBatchGatesAsync",
            "the run-detail gate colouring reads every gate's own outcome via ListBatchGatesAsync.");
        methods.Select(m => m.Name).Should().Contain("QueryRunsAsync",
            "the Executions Runs view and Batches/Detail recent runs read the run-store via QueryRunsAsync.");
        methods.Select(m => m.Name).Should().Contain("CancelRunAsync",
            "the run-detail Cancel run button trips an in-flight run via CancelRunAsync.");
        methods.Select(m => m.Name).Should().Contain("RetryRunAsync",
            "the run-detail Retry button restarts a Failed run from its failed step via RetryRunAsync.");
        methods.Select(m => m.Name).Should().Contain("SetScheduleEnabledAsync",
            "the batch-detail Pause/Resume schedule toggle calls SetScheduleEnabledAsync.");
    }

    [Fact]
    public void IUKBatchClient_TotalSurface_Is_40_Members()
    {
        // Combined: 2 properties + 5 events + 33 methods = 40 reflection members.
        var t = typeof(IUKBatchClient);
        var propCount = t.GetProperties(BindingFlags.Instance | BindingFlags.Public).Length;
        var eventCount = t.GetEvents(BindingFlags.Instance | BindingFlags.Public).Length;
        var methodCount = t.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => !m.IsSpecialName)
            .Where(m => m.DeclaringType == typeof(IUKBatchClient))
            .Count();
        (propCount + eventCount + methodCount).Should().Be(40,
            "IUKBatchClient surface: 2 props + 5 events + 33 methods = 40 members.");
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
            "BatchRunRegistry.",
            "BatchScheduler.",
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
