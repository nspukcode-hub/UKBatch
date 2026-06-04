using System.Text.Json;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using UKBatch.Abstractions.Batches;
using UKBatch.Api.Batches;
using UKBatch.Api.Common;
using UKBatch.Api.Jobs;
using UKBatch.Dashboard.Clients;
using UKBatch.Dashboard.Components.Pages.Batches;
using UKBatch.Dashboard.Models;
using UKBatch.Dashboard.Models.Wizard;
using UKBatch.Dashboard.Tests.Pages.Common;
using Xunit;

namespace UKBatch.Dashboard.Tests.Components;

/// <summary>
/// Editor ⇄ Wizard round-trip parity. Proves the visual Editor and the
/// guided Wizard share ONE model (<see cref="BatchWizardModel"/> + <see cref="WizardStepDraft"/>) and
/// never drift: a structure built the way the Editor builds one round-trips through the Wizard's
/// <see cref="BatchWizardModel.FromDefinition"/> with identical step structure, and a hint-less
/// Wizard-created definition opens in the Editor laid-out without error.
/// </summary>
/// <remarks>
/// <para>CONTRACT FINDING (the reason these tests use a custom structural comparer instead of raw
/// <c>BatchStep ==</c>): <see cref="BatchStep"/> is a <c>record class</c>, BUT its compiler-generated
/// value-equality uses REFERENCE equality for its <c>IReadOnlyList</c>/<c>IReadOnlyDictionary</c>
/// members (<see cref="JobStepData.Parameters"/>, <see cref="ParallelGroupData.Steps"/>,
/// <see cref="ApprovalGateConfig.AllowedRoles"/>). So two <c>BatchStep</c>s projected from the same
/// model via <see cref="WizardStepDraft.ToBatchStep"/> are <c>==</c> ONLY when every collection is
/// <c>null</c> (a bare Job). A ParallelGroup, an ApprovalGate with roles, or a parameterized Job
/// compares <c>!=</c> by value even when structurally identical. Empirically verified during.
/// Therefore the parity assertion recurses into the collections (<see cref="AssertStepsEqual"/>);
/// a naive <c>Should.Equal(...)</c> would be a false-RED here, and restricting the fixture to
/// bare Jobs (to make <c>==</c> hold) would be a false-GREEN that never exercises the group/approval
/// projection — the part most likely to drift.</para>
/// <para>SECOND CONTRACT NOTE (why the hint assertions go through <see cref="JsonRoundTrip"/>):
/// <c>DagLayoutHintsSerializer.Serialize</c> emits an inner shape of
/// <c>Dictionary&lt;string, Dictionary&lt;string, double&gt;&gt;</c>, but <c>Parse</c>'s in-memory
/// branch only reads <c>IDictionary&lt;string, object?&gt;</c>. So the IN-MEMORY <c>Serialize → Parse</c>
/// path returns empty BY DESIGN — production never hits it, because Metadata always crosses
/// System.Text.Json (EF JSON column / REST) before being re-parsed (the value re-materializes as a
/// <c>JsonElement</c>, which <c>Parse</c> handles). This matches <c>DagLayoutHintsSerializerTests</c>
/// #14. The opaque carry (the <c>MetadataKey</c> entry survives <c>FromDefinition</c>) is the actual
/// guarantee and is asserted directly; coordinate survival is asserted through the wire hop.</para>
/// </remarks>
public sealed class EditorWizardParityTests : TestContext
{
    public EditorWizardParityTests()
    {
        // The Editor's DrawflowCanvas imports dag-editor.js in OnAfterRender; Loose mode returns defaults.
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private const string Svc = "svc";

    // ── Editor-built model → Wizard load → structurally identical ──────────────

    [Fact]
    public void EditorCreate_ThenWizardLoad_StructurallyIdentical()
    {
        // Build a BatchWizardModel exactly the way the Editor builds one: Name + a Job step + a
        // ParallelGroup (2 Job children) + an ApprovalGate, with layout hints serialized into Metadata
        // (the Editor's SaveAsync sets _model.Metadata = Serialize(_localHints) before projecting).
        var jobStep = new WizardStepDraft { StepType = BatchStepType.Job, JobName = "JobA" };
        var group = new WizardStepDraft { StepType = BatchStepType.ParallelGroup, JoinPolicy = ParallelJoinPolicy.WaitAll };
        group.Children.Add(new WizardStepDraft { StepType = BatchStepType.Job, JobName = "JobB" });
        group.Children.Add(new WizardStepDraft { StepType = BatchStepType.Job, JobName = "JobC", TargetService = "worker" });
        var gate = new WizardStepDraft
        {
            StepType = BatchStepType.ApprovalGate,
            ApprovalTitle = "Manager sign-off",
            AllowedRoles = { "ops", "lead" },
        };

        var hints = new Dictionary<string, DagLayoutHint>(StringComparer.Ordinal)
        {
            [jobStep.StepId] = new DagLayoutHint { X = 120, Y = 80 },
            [group.StepId] = new DagLayoutHint { X = 300, Y = 240 },
            [gate.StepId] = new DagLayoutHint { X = 140, Y = 400 },
        };

        var original = new BatchWizardModel { Name = "rt-batch" };
        original.Steps.Add(jobStep);
        original.Steps.Add(group);
        original.Steps.Add(gate);
        original.Metadata = DagLayoutHintsSerializer.Serialize(hints, existingMetadata: null);

        // Project to the create request (Editor's create path) then reconstruct the DTO the server
        // would echo back (same Steps/OnFailure/Name/Source/FailurePolicy/Schedule/Metadata, id+version).
        var create = original.ToCreateRequest(createdBy: null);
        var echoed = new BatchDefinitionDto
        {
            Id = "rt",
            Version = 1,
            Name = create.Name,
            Source = create.Source,
            Schedule = create.Schedule,
            Steps = create.Steps,
            OnFailureSteps = create.OnFailureSteps,
            FailurePolicy = create.FailurePolicy,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Metadata = create.Metadata,
        };

        // Wizard load.
        var reloaded = BatchWizardModel.FromDefinition(echoed);

        // Structural parity: the projected BatchStep lists must match by VALUE (recursing into
        // collections), preserving order, type, and each variant's payload.
        AssertStepsEqual(reloaded.StepsAsBatchSteps(), original.StepsAsBatchSteps());
        AssertStepsEqual(reloaded.OnFailureAsBatchSteps(), original.OnFailureAsBatchSteps());

        // Sanity on the scalar header fields the Wizard surfaces.
        reloaded.Name.Should().Be(original.Name);
        reloaded.Source.Should().Be(original.Source);
        reloaded.FailurePolicy.Should().Be(original.FailurePolicy);

        // Metadata (layout hints) carried opaquely through the round-trip. The carry is opaque:
        // FromDefinition copies the dict by reference, so the in-memory Metadata still holds the
        // serializer-internal MetadataKey entry verbatim.
        reloaded.Metadata.Should().NotBeNull("the Wizard load preserves operator-set layout hints");
        reloaded.Metadata!.Should().ContainKey(DagLayoutHintsSerializer.MetadataKey,
 "the layout-hints key survives the Editor→Wizard round-trip as opaque carry ");

        // To confirm the actual coordinates survive, parse through the PRODUCTION wire path: Metadata
        // is only re-parsed after a System.Text.Json round-trip (EF JSON column / REST). The in-memory
        // Serialize output's inner shape (Dictionary<string,double>) is a serializer-internal detail
        // that Parse does NOT read directly — only the JsonElement wire shape (see
        // DagLayoutHintsSerializerTests #14). Mirroring that here keeps the parity assertion honest.
        // (ContainKeys(params TKey[]) has no because-string overload — assert the count + each key.)
        var wireParsed = DagLayoutHintsSerializer.Parse(JsonRoundTrip(reloaded.Metadata!));
        wireParsed.Keys.Should().BeEquivalentTo(
            new[] { jobStep.StepId, group.StepId, gate.StepId },
            "all three dragged positions survive the Editor→Wizard round-trip through the wire");
    }

    /// <summary>
    /// Round-trips a Metadata dict through System.Text.Json — the EXACT path EF Core JSON columns and
    /// the REST API take before <c>DagLayoutHintsSerializer.Parse</c> sees it in production.
    /// (The serializer's in-memory output shape is deliberately not re-parsable without this hop; see
    /// the contract note on this class + DagLayoutHintsSerializerTests #14.)
    /// </summary>
    private static IReadOnlyDictionary<string, object?> JsonRoundTrip(IReadOnlyDictionary<string, object?> metadata)
    {
        var json = JsonSerializer.Serialize(metadata);
        using var doc = JsonDocument.Parse(json);
        var rebuilt = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            rebuilt[prop.Name] = prop.Value.Clone();
        }
        return rebuilt;
    }

    // ── Wizard-created (hint-less) → Editor opens auto-laid-out, no error ──────

    [Fact]
    public void WizardCreate_ThenEditorLoad_AutoLayoutsWithoutError()
    {
        // A Wizard-created definition has NO layout hints (Metadata == null). The Editor must open it
        // laid-out via BuildGraph's auto-layout fallback — canvas present, no fallback banner, and one
        // rail chip per top-level step.
        var client = WireDeps();
        var hintless = new BatchDefinitionDto
        {
            Id = "wiz-id",
            Name = "wizard-made",
            Source = BatchSource.Dashboard,
            Version = 2,
            Steps =
            [
                new BatchStep
                {
                    StepId = "s1", Order = 0, StepType = BatchStepType.Job,
                    Job = new JobStepData { JobName = "JobA" },
                },
                new BatchStep
                {
                    StepId = "g1", Order = 1, StepType = BatchStepType.ParallelGroup,
                    ParallelGroup = new ParallelGroupData
                    {
                        JoinPolicy = ParallelJoinPolicy.WaitAll,
                        Steps =
                        [
                            new BatchStep { StepId = "c1", Order = 0, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "JobA" } },
                            new BatchStep { StepId = "c2", Order = 1, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "JobA" } },
                        ],
                    },
                },
                new BatchStep
                {
                    StepId = "a1", Order = 2, StepType = BatchStepType.ApprovalGate,
                    Approval = new ApprovalGateConfig
                    {
                        Title = "Gate", AllowedRoles = ["ops"], OnTimeout = ApprovalTimeoutAction.Fail,
                    },
                },
            ],
            FailurePolicy = BatchFailurePolicy.StopOnFailure,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Metadata = null, // Wizard never sets hints — this is the auto-layout fallback path.
        };
        client.GetBatchByIdAsync("wiz-id", Arg.Any<CancellationToken>()).Returns(hintless);

        var cut = RenderComponent<Editor>(p => p
            .Add(e => e.ServiceName, Svc)
            .Add(e => e.BatchId, "wiz-id"));

        cut.WaitForState(() => cut.FindAll("div.dag-ed-canvas").Count > 0
                            || cut.FindAll("div.dag-ed-fallback").Count > 0);

        cut.FindAll("div.dag-ed-canvas").Should().NotBeEmpty(
            "a hint-less (Wizard-created) batch must open laid-out via BuildGraph's auto-layout — no degrade");
        cut.FindAll("div.dag-ed-fallback").Should().BeEmpty(
            "auto-layout of hint-less nodes must NOT throw / fall back to the wizard banner");

        // The order-rail shows one chip per TOP-LEVEL step (3: Job, ParallelGroup, ApprovalGate). The
        // group's children are inspector-only, never rail chips.
        cut.FindAll("div.dag-ed-rail__chip").Count.Should().Be(3,
            "BuildGraph iterates top-level steps only — a ParallelGroup is one chip, not one-per-child");
    }

    // ── Wizard edit preserves Editor-set layout hints (opaque carry) ─────

    [Fact]
    public void WizardEdit_PreservesLayoutHints()
    {
        // A Wizard edit must NOT clobber hints an operator set in the visual Editor: FromDefinition →
        // ToUpdateRequest must still carry the "dashboard.layoutHints" key verbatim.
        var hints = new Dictionary<string, DagLayoutHint>(StringComparer.Ordinal)
        {
            ["s1"] = new DagLayoutHint { X = 42, Y = 7 },
        };
        var metadata = DagLayoutHintsSerializer.Serialize(hints, existingMetadata: null);
        metadata.Should().ContainKey(DagLayoutHintsSerializer.MetadataKey);

        var dto = new BatchDefinitionDto
        {
            Id = "id", Name = "b", Source = BatchSource.Dashboard, Version = 4,
            Steps =
            [
                new BatchStep { StepId = "s1", Order = 0, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "JobA" } },
            ],
            FailurePolicy = BatchFailurePolicy.StopOnFailure,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Metadata = metadata,
        };

        var update = BatchWizardModel.FromDefinition(dto).ToUpdateRequest();

        update.Metadata.Should().NotBeNull("a Wizard edit must round-trip Metadata");
        update.Metadata!.Should().ContainKey(DagLayoutHintsSerializer.MetadataKey,
            "a Wizard edit must NOT discard operator-set layout hints — they survive as opaque carry");
        // Confirm the hinted step id survives through the production wire path (System.Text.Json),
        // which is the only path Parse reads — the in-memory carry is opaque (see the class contract
        // note + DagLayoutHintsSerializerTests #14).
        DagLayoutHintsSerializer.Parse(JsonRoundTrip(update.Metadata!)).Should().ContainKey("s1",
            "the actual hinted step id must still parse out after the Wizard round-trip over the wire");
    }

    // ── harness (mirrors EditorTests.WireDeps exactly) ───────────────────────────────

    private IUKBatchClient WireDeps()
    {
        var registry = PageTestHelpers.RegistryWith(PageTestHelpers.Descriptor(Svc));
        var client = PageTestHelpers.BuildClient();
        client.ListJobsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>())
            .Returns(new PageEnvelope<JobDefinitionDto>
            {
                Items =
                [
                    new JobDefinitionDto
                    {
                        Name = "JobA", IsPartitioned = false, MaxRetries = 0, TimeoutSeconds = 0,
                        DefaultParameters = new Dictionary<string, object?>(), Tags = [],
                    },
                ],
                TotalCount = 1, Offset = 0, Limit = 500,
            });
        var factory = PageTestHelpers.FactoryFor(Svc, client);
        Services.AddSingleton(registry);
        Services.AddSingleton(factory);
        Services.AddSingleton(PageTestHelpers.NewState());
        Services.AddSingleton(PageTestHelpers.NewNotifications());
        return client;
    }

    // ── structural BatchStep comparison (deep — collections are reference-equal under record ==) ──

    private static void AssertStepsEqual(IReadOnlyList<BatchStep> actual, IReadOnlyList<BatchStep> expected)
    {
        actual.Count.Should().Be(expected.Count, "the two UIs must emit the same number of steps");
        for (var i = 0; i < expected.Count; i++)
        {
            AssertStepEqual(actual[i], expected[i], $"step[{i}]");
        }
    }

    private static void AssertStepEqual(BatchStep actual, BatchStep expected, string because)
    {
        actual.StepId.Should().Be(expected.StepId, $"{because}: StepId");
        actual.Order.Should().Be(expected.Order, $"{because}: Order");
        actual.StepType.Should().Be(expected.StepType, $"{because}: StepType");

        // Job payload.
        (actual.Job is null).Should().Be(expected.Job is null, $"{because}: Job presence");
        if (expected.Job is { } ej)
        {
            var aj = actual.Job!;
            aj.JobName.Should().Be(ej.JobName, $"{because}: Job.JobName");
            aj.TargetService.Should().Be(ej.TargetService, $"{because}: Job.TargetService");
            aj.MaxRetries.Should().Be(ej.MaxRetries, $"{because}: Job.MaxRetries");
            aj.TimeoutSeconds.Should().Be(ej.TimeoutSeconds, $"{because}: Job.TimeoutSeconds");
            // Parameters: dict equality by content (reference-equal under record ==).
            (aj.Parameters is null).Should().Be(ej.Parameters is null, $"{because}: Job.Parameters presence");
            if (ej.Parameters is not null)
            {
                aj.Parameters!.Should().BeEquivalentTo(ej.Parameters, $"{because}: Job.Parameters content");
            }
        }

        // ParallelGroup payload (recurse into children).
        (actual.ParallelGroup is null).Should().Be(expected.ParallelGroup is null, $"{because}: ParallelGroup presence");
        if (expected.ParallelGroup is { } eg)
        {
            var ag = actual.ParallelGroup!;
            ag.JoinPolicy.Should().Be(eg.JoinPolicy, $"{because}: ParallelGroup.JoinPolicy");
            AssertStepsEqual(ag.Steps, eg.Steps);
        }

        // ApprovalGate payload.
        (actual.Approval is null).Should().Be(expected.Approval is null, $"{because}: Approval presence");
        if (expected.Approval is { } ea)
        {
            var aa = actual.Approval!;
            aa.Title.Should().Be(ea.Title, $"{because}: Approval.Title");
            aa.Description.Should().Be(ea.Description, $"{because}: Approval.Description");
            aa.TimeoutAfter.Should().Be(ea.TimeoutAfter, $"{because}: Approval.TimeoutAfter");
            aa.OnTimeout.Should().Be(ea.OnTimeout, $"{because}: Approval.OnTimeout");
            aa.AllowedRoles.Should().Equal(ea.AllowedRoles, $"{because}: Approval.AllowedRoles");
        }
    }
}
