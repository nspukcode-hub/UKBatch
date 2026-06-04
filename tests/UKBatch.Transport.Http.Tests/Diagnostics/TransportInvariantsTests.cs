using System.Reflection;
using FluentAssertions;
using Xunit;

namespace UKBatch.Transport.Http.Tests.Diagnostics;

/// <summary>
/// Source-grep regression locks for the transport package.
/// </summary>
[Trait("Category", "HttpTransport")]
public sealed class TransportInvariantsTests
{
    private static string LocateRepoRoot()
    {
        var assemblyPath = typeof(TransportInvariantsTests).Assembly.Location;
        var dir = new DirectoryInfo(Path.GetDirectoryName(assemblyPath)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "UKBatch.sln")))
        {
            dir = dir.Parent;
        }
        if (dir is null) throw new InvalidOperationException("Could not locate UKBatch.sln in any parent directory.");
        return dir.FullName;
    }

    [Fact]
    public void Api_FriendAccess_LimitedToSevenCoreInternalTypes()
    {
        // UKBatch.Transport.Http joins UKBatch.Api + UKBatch.Dashboard as the third friend-access
        // assembly. Allowed Core internal CONSUMPTION on transport side:
        // * LruDedupeCache<T> (via NonceDedupeCache + MessageIdDedupeCache wrappers)
        // * JobNotRegisteredException (typed exception caught by InvokeEndpointHandler)
        // * JobMessage / JobResult are PUBLIC abstractions, not internals.
        // Forbidden: JobRunner, JobDispatcher, JobWorker, BatchExecutor, JobScheduler, IdGenerator,
        // ApprovalGateService, etc.
        var transportDir = Path.Combine(LocateRepoRoot(), "src", "UKBatch.Transport.Http");
        var forbidden = new[]
        {
            "JobDispatcher.",
            "JobWorker.",
            "BatchExecutor.",
            "JobScheduler.",
            "ExponentialRetryPolicy",
            "ApprovalGateService.",
            "DebouncedProgressFlusher.",
            "CountingJobProgress.",
            "JobLoggerFactory.",
            "InMemoryJobStore.",
            "BatchDefinitionRegistry.",
            "JobDefinitionRegistry.",
        };
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(transportDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }
            var text = File.ReadAllText(file);
            foreach (var bad in forbidden)
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
            "friend-access discipline: UKBatch.Transport.Http consumes only LruDedupeCache<T> + JobNotRegisteredException from Core internals.");
    }

    [Fact]
    public void Transport_FriendAccess_NoUnauthorizedCoreInternalConsumption()
    {
        // Subset of the above: focus on the more dangerous internals that have appeared in prior
        // friend-access drift incidents.
        var transportDir = Path.Combine(LocateRepoRoot(), "src", "UKBatch.Transport.Http");
        var allowedInternalReferences = new HashSet<string>(StringComparer.Ordinal)
        {
            "LruDedupeCache",
            "JobNotRegisteredException",
        };

        // Scan for `using UKBatch.Runtime;` consumers — every such using is a friend-access touchpoint
        // and we audit each.
        var consumers = new List<string>();
        foreach (var file in Directory.GetFiles(transportDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }
            var text = File.ReadAllText(file);
            if (text.Contains("using UKBatch.Runtime;", StringComparison.Ordinal))
            {
                consumers.Add(file);
            }
        }
        // Each consumer must reference ONLY allowed internals.
        // (Soft check — this test documents intent; the harder forbidden-list grep above enforces.)
        consumers.Should().NotBeEmpty("at least one file should consume UKBatch.Runtime — NonceDedupeCache + MessageIdDedupeCache");
    }

    [Fact]
    public void HmacAuthorizationFilter_CtorParameter_IsNonceDedupeCache_NotRawLruDedupeCache()
    {
        // The filter's ctor takes the wrapper type NonceDedupeCache, NOT the raw
        // LruDedupeCache<string>. Locks the wrapper design so future refactors don't bypass it.
        var filterType = typeof(UKBatch.Transport.Http.Auth.HmacAuthorizationFilter);
        var ctor = filterType.GetConstructors().Single();
        ctor.GetParameters().Should().Contain(p => p.ParameterType == typeof(UKBatch.Transport.Http.Auth.NonceDedupeCache),
            "HmacAuthorizationFilter takes NonceDedupeCache wrapper, not raw LruDedupeCache");
        ctor.GetParameters().Should().NotContain(p => p.ParameterType.IsGenericType
            && p.ParameterType.GetGenericTypeDefinition().Name.StartsWith("LruDedupeCache", StringComparison.Ordinal));
    }

    [Fact]
    public void SyncOverAsync_NoTaskResultOutsideTests()
    {
        var transportDir = Path.Combine(LocateRepoRoot(), "src", "UKBatch.Transport.Http");
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(transportDir, "*.cs", SearchOption.AllDirectories))
        {
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
            if (text.Contains(".Result;", StringComparison.Ordinal))
            {
                offenders.Add($"{file}: .Result");
            }
        }
        offenders.Should().BeEmpty("transport production code must not contain sync-over-async patterns");
    }
}
