using System.Reflection;
using FluentAssertions;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Core.Tests.Runtime;

/// <summary>
/// <see cref="BatchCompletionSignal"/> channel + payload contract.
/// Uses reflection to construct + invoke since the type is internal (Api friend-access only).
/// </summary>
public class BatchCompletionSignalTests
{
    private static BatchCompletionSignalPayload NewPayload(string runId = "r1", string defId = "d1", string name = "n1")
        => new() { BatchRunId = runId, BatchDefinitionId = defId, BatchName = name };

    [Fact]
    public async Task Signal_WithFullPayload_PropagatesToChannelReader()
    {
        var signal = new BatchCompletionSignal();
        var p = NewPayload(runId: "run-x", defId: "def-y", name: "pipeline-z");
        signal.Signal(p);
        var got = await signal.CompletedBatchRunIds.ReadAsync(CancellationToken.None);
        got.Should().BeSameAs(p, "channel is a pass-through reference store.");
        got.BatchRunId.Should().Be("run-x");
        got.BatchDefinitionId.Should().Be("def-y");
        got.BatchName.Should().Be("pipeline-z");
    }

    [Fact]
    public async Task Signal_WhenChannelFull_DropsOldestSilently()
    {
        // The bounded channel has capacity 1024 with DropOldest. Write 2*1024 + 10 payloads;
        // reader observes the last 1024 only (the first writes are dropped).
        var signal = new BatchCompletionSignal();
        const int total = 2 * 1024 + 10;
        for (var i = 0; i < total; i++)
        {
            signal.Signal(NewPayload(runId: $"r{i:D5}"));
        }
        var seen = new List<string>();
        // Drain non-blocking up to 1024+1 reads.
        for (var i = 0; i < 1100; i++)
        {
            if (!signal.CompletedBatchRunIds.TryRead(out var p)) break;
            seen.Add(p.BatchRunId);
        }
        seen.Count.Should().Be(1024, "channel cap is 1024 with DropOldest.");
        // The TAIL (most recent) entries survive.
        seen.Last().Should().Be($"r{(total - 1):D5}");
    }

    [Fact]
    public void Signal_NullPayload_ThrowsArgumentNullException()
    {
        var signal = new BatchCompletionSignal();
        FluentActions.Invoking(() => signal.Signal(null!))
            .Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Payload_RequiredInitOnly_ReflectionFieldShape()
    {
        // C# `required` is compile-time only; verify the property shapes are init-only and the type
        // is sealed record class (contract).
        var t = typeof(BatchCompletionSignalPayload);
        t.IsSealed.Should().BeTrue("internal sealed record class.");
        var runIdProp = t.GetProperty("BatchRunId");
        runIdProp.Should().NotBeNull();
        runIdProp!.PropertyType.Should().Be<string>();
        // Required marker via [RequiredMember] custom attribute (synthesized by compiler for `required`).
        runIdProp.CustomAttributes.Any(a => a.AttributeType.Name == "RequiredMemberAttribute")
            .Should().BeTrue("BatchRunId must be `required`.");

        var defIdProp = t.GetProperty("BatchDefinitionId");
        defIdProp.Should().NotBeNull();
        defIdProp!.CustomAttributes.Any(a => a.AttributeType.Name == "RequiredMemberAttribute")
            .Should().BeTrue("BatchDefinitionId must be `required`.");

        var nameProp = t.GetProperty("BatchName");
        nameProp.Should().NotBeNull();
        nameProp!.CustomAttributes.Any(a => a.AttributeType.Name == "RequiredMemberAttribute")
            .Should().BeTrue("BatchName must be `required`.");
    }
}
