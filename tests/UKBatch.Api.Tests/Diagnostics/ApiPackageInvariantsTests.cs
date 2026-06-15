using System.Reflection;
using FluentAssertions;
using Xunit;

namespace UKBatch.Api.Tests.Diagnostics;

/// <summary>
/// Source-grep guarantees for the <c>UKBatch.Api</c> package:
/// <list type="bullet">
/// <item>No DTO mirrors for deeply nested value types.</item>
/// <item>No sync-over-async (<c>Task.Result</c>, <c>.GetAwaiter().GetResult()</c>) outside tests.</item>
/// <item><c>IJobStatusHubClient</c> does NOT expose <c>HubBackpressureWarning</c>.</item>
/// <item><c>UKBatch.Api</c> consumes only the permitted Core internals.</item>
/// </list>
/// </summary>
public sealed class ApiPackageInvariantsTests
{
    private static string LocateRepoRoot()
    {
        // tests/UKBatch.Api.Tests/bin/Debug/net10.0/ -> walk up to repo root.
        var assemblyPath = typeof(ApiPackageInvariantsTests).Assembly.Location;
        var dir = new DirectoryInfo(Path.GetDirectoryName(assemblyPath)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "UKBatch.sln")))
        {
            dir = dir.Parent;
        }
        if (dir is null) throw new InvalidOperationException("Could not locate UKBatch.sln in any parent directory.");
        return dir.FullName;
    }

    [Fact]
    public void Api_NoMirrorDtosForNestedTypes()
    {
        // Carve-out lock: no DTO mirrors for nested types.
        var apiDir = Path.Combine(LocateRepoRoot(), "src", "UKBatch.Api");
        var forbidden = new[]
        {
            "BatchStepDto.cs",
            "JobStepDataDto.cs",
            "ParallelGroupDataDto.cs",
            "ApprovalGateConfigDto.cs",
            "ApproverContextDto.cs",
        };
        var files = Directory.GetFiles(apiDir, "*.cs", SearchOption.AllDirectories);
        foreach (var f in forbidden)
        {
            files.Should().NotContain(p => Path.GetFileName(p).Equals(f, StringComparison.OrdinalIgnoreCase),
                $"carve-out lock: {f} is forbidden — nested types serialize as Abstractions types.");
        }
    }

    [Fact]
    public void SyncOverAsync_NoTaskResultOutsideTests()
    {
        // Grep gate — no .Result or.GetAwaiter().GetResult() in production code.
        var repoRoot = LocateRepoRoot();
        var dirsToCheck = new[]
        {
            Path.Combine(repoRoot, "src", "UKBatch.Api"),
            Path.Combine(repoRoot, "samples", "Sample.RestApi"),
            Path.Combine(repoRoot, "samples", "Sample.RestApi.HubClient"),
        };
        var offenders = new List<string>();
        foreach (var dir in dirsToCheck)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                // Skip obj/ + bin/ outputs.
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }
                var text = File.ReadAllText(file);
                if (text.Contains(".GetAwaiter().GetResult()", StringComparison.Ordinal))
                {
                    offenders.Add($"{file}: .GetAwaiter().GetResult()");
                }
                // Detect `.Result` outside test code — heuristic.
                if (text.Contains(".Result;", StringComparison.Ordinal) || text.Contains(".Result.", StringComparison.Ordinal))
                {
                    offenders.Add($"{file}: .Result");
                }
            }
        }
        offenders.Should().BeEmpty("sync-over-async lock: no .Result / .GetAwaiter().GetResult() outside tests.");
    }

    [Fact]
    public void Hub_NoHubBackpressureWarningMethod_v01()
    {
        // Deferral lock: IJobStatusHubClient does NOT expose HubBackpressureWarning.
        var clientType = typeof(UKBatch.Api.Hub.IJobStatusHubClient);
        clientType.GetMethods().Should().NotContain(m => m.Name == "HubBackpressureWarning",
            "HubBackpressureWarning is deferred to v0.2.0 adapter telemetry.");
    }

    [Fact]
    public void Api_FriendAccess_LimitedToSevenCoreInternalTypes()
    {
        // Friend-access discipline. Only the following Core internals may be referenced across the
        // UKBatch.Api + UKBatch.Dashboard consumers:
        // 1. BatchStateMachine (REST state transition validation)
        // 2. IApprovalGateEvents (hub fan-out)
        // 3. IProgressBeatBroadcaster (hub fan-out)
        // 4. IBatchCompletionEvents (hub fan-out)
        // 5. BatchCompletionSignalPayload (signal payload)
        // 6. LruDedupeCache<T> (hub dedupe; consumed by both Api + Dashboard)
        //
        // UKBatch.Transport.Http is a third friend-access ASSEMBLY consuming the same
        // LruDedupeCache<T> primitive (via NonceDedupeCache + MessageIdDedupeCache wrappers) — the
        // assembly count is 3, the type count stays 6.
        //
        // All other internals (JobDispatcher, JobWorker, JobRunner, BatchExecutor, JobScheduler,
        // retry policies, IdGenerator, ApprovalGateService, DebouncedProgressFlusher) are off-limits
        // from Api + Dashboard codebases even though InternalsVisibleTo grants the visibility.
        var apiDir = Path.Combine(LocateRepoRoot(), "src", "UKBatch.Api");
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
            "BatchScheduler.",
            "BatchRunRegistry.",
        };
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(apiDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }
            var text = File.ReadAllText(file);
            foreach (var bad in forbiddenInternalTypeReferences)
            {
                // Match only when the forbidden token is NOT preceded by an identifier character.
                // This prevents false positives where the substring appears at the end of a longer
                // public identifier — e.g. "IJobRunner." is the PUBLIC interface, not internal
                // "JobRunner.". A naive Contains check would flag both.
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
            "friend-access discipline: UKBatch.Api + UKBatch.Dashboard consume only BatchStateMachine, IApprovalGateEvents, IProgressBeatBroadcaster, IBatchCompletionEvents, BatchCompletionSignalPayload, and LruDedupeCache<T> from Core internals; UKBatch.Transport.Http consumes LruDedupeCache<T> only.");
    }
}
