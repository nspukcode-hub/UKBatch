using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using UKBatch.Abstractions.Batches;
using UKBatch.Api.Batches;
using UKBatch.Dashboard.Models.Wizard;
using UKBatch.Dashboard.Tests.Common;
using Xunit;

namespace UKBatch.Dashboard.Tests.Models;

/// <summary>
/// Parity between <see cref="BatchDefinitionClientValidator"/> and the
/// server-side <c>BatchDefinitionValidator</c>. The wizard's local validator MUST surface the same
/// SET of property-paths as the server for wizard-emittable models, so <c>Next</c> gating mirrors
/// the round-trip rejection (message wording is NOT a contract — only the path-set is, per the
/// parity-harness honesty lesson).
/// </summary>
/// <remarks>
/// <para><b>Scope constraint:</b> the matrix is restricted to wizard-emittable models. Server-only
/// paths the wizard cannot reach are documented and excluded:</para>
/// <list type="bullet">
/// <item><c>Steps[i].Job/ParallelGroup/Approval</c> null-payload (wizard always emits a payload).</item>
/// <item><c>Steps[i].ParallelGroup.JoinPolicy</c> / <c>FailurePolicy</c> <c>Enum.IsDefined</c> (wizard dropdowns).</item>
/// <item><c>Id</c> non-empty (server assigns it on POST).</item>
/// </list>
/// <para>The server validator runs inside <c>POST /batches</c>; we hit the WAF and read the
/// <c>errors</c> dict from the <c>ValidationProblemDetails</c> 400 response.</para>
/// </remarks>
public sealed class BatchDefinitionClientValidatorTests : IClassFixture<SampleRestApiFactory>
{
    private readonly SampleRestApiFactory _factory;

    public BatchDefinitionClientValidatorTests(SampleRestApiFactory factory)
    {
        _factory = factory;
    }

    // ── Wizard-emittable matrix ──────────────────────────────────────────────────

    public static IEnumerable<object[]> ParityMatrix => new List<object[]>
    {
        new object[]
        {
            "blank Name",
            new BatchWizardModel
            {
                Name = string.Empty,
                Steps = { JobDraft("s1", "Echo") },
            },
        },
        new object[]
        {
            "empty Steps",
            new BatchWizardModel
            {
                Name = "ok",
                Steps = { },
            },
        },
        new object[]
        {
            "blank Job.JobName",
            new BatchWizardModel
            {
                Name = "ok",
                Steps = { JobDraft("s1", jobName: string.Empty) },
            },
        },
        new object[]
        {
            "ParallelGroup with one child",
            new BatchWizardModel
            {
                Name = "ok",
                Steps = { ParallelDraft("pg", new[] { JobDraft("c1", "ChildA") }) },
            },
        },
        new object[]
        {
            "WaitMajority with two children (<3)",
            new BatchWizardModel
            {
                Name = "ok",
                Steps =
                {
                    ParallelDraft("pg",
                        new[] { JobDraft("c1", "A"), JobDraft("c2", "B") },
                        ParallelJoinPolicy.WaitMajority),
                },
            },
        },
        new object[]
        {
            "blank ApprovalGate Title",
            new BatchWizardModel
            {
                Name = "ok",
                Steps = { ApprovalDraft("ag1", title: string.Empty) },
            },
        },
        new object[]
        {
            "blank Name AND empty Steps",
            new BatchWizardModel
            {
                Name = string.Empty,
                Steps = { },
            },
        },
    };

    [Theory]
    [MemberData(nameof(ParityMatrix))]
    public async Task Validate_PathSetEqualsServer_ForWizardEmittableModels(string scenario, BatchWizardModel model)
    {
        _ = scenario; // for [Theory] display only — the assertion message is below.

        // Build the client-side error path-set.
        var clientErrors = BatchDefinitionClientValidator.Validate(model);

        // Build the equivalent CreateBatchRequest and POST through the WAF — server validator runs
        // inside POST /batches and returns its `errors` dict on 400.
        var request = model.ToCreateRequest(createdBy: null);
        using var http = _factory.CreateClient();
        http.BaseAddress = new Uri(_factory.Server.BaseAddress, "/api/");
        using var res = await http.PostAsJsonAsync("batches", request);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            $"scenario '{scenario}' must fail server-side validation");

        var problem = await res.Content.ReadFromJsonAsync<ValidationProblemEnvelope>();
        problem.Should().NotBeNull();
        problem!.Errors.Should().NotBeNull("400 must carry an errors dict for field-level mapping");

        // Discard the SERVER-ONLY paths the wizard can't reach (honesty constraint).
        var serverPaths = problem.Errors!.Keys.Where(IsWizardEmittablePath).ToHashSet(StringComparer.Ordinal);
        var clientPaths = clientErrors.Keys.ToHashSet(StringComparer.Ordinal);

        clientPaths.Should().BeEquivalentTo(serverPaths,
            $"scenario '{scenario}': client validator path-set MUST match the server's (wizard-emittable paths only). " +
            $"Client: [{string.Join(",", clientPaths)}]  Server: [{string.Join(",", serverPaths)}]");
    }

    // ── OnFailureSteps coverage (server validator has a gap here, but the client
    // MUST surface blank fields so the operator catches them before submit) ─────────────

    [Fact]
    public void Validate_OnFailureSteps_BlankJobName_ReportsPath()
    {
        // the wizard validator MUST report invalid OnFailureSteps (blank JobName etc.)
        // even though the server's BatchDefinitionValidator currently ignores OnFailureSteps. This
        // is a wizard-only safety net; the operator should not ship a runtime-fail definition.
        var model = new BatchWizardModel
        {
            Name = "ok",
            FailurePolicy = BatchFailurePolicy.Compensate,
            Steps = { JobDraft("s1", "Echo") },
            OnFailureSteps = { JobDraft("f1", jobName: string.Empty) },
        };

        var errors = BatchDefinitionClientValidator.Validate(model);

        errors.Should().ContainKey("OnFailureSteps[0].Job.JobName",
 "wizard must surface OnFailureSteps validation");
    }

    [Fact]
    public void Validate_OnFailureSteps_AllValid_ProducesNoErrors()
    {
        // Inverse: a valid OnFailureSteps list must NOT raise spurious errors.
        var model = new BatchWizardModel
        {
            Name = "ok",
            FailurePolicy = BatchFailurePolicy.Compensate,
            Steps = { JobDraft("s1", "Echo") },
            OnFailureSteps = { JobDraft("f1", "Rollback") },
        };

        var errors = BatchDefinitionClientValidator.Validate(model);

        errors.Should().NotContainKey("OnFailureSteps[0].Job.JobName");
    }

    // ── Sanity: a fully-valid wizard-emittable model produces zero errors ────────

    [Fact]
    public void Validate_FullyValidModel_ProducesNoErrors()
    {
        var model = new BatchWizardModel
        {
            Name = "ok",
            Steps =
            {
                JobDraft("s1", "Echo"),
                ParallelDraft("pg",
                    new[] { JobDraft("c1", "A"), JobDraft("c2", "B"), JobDraft("c3", "C") },
                    ParallelJoinPolicy.WaitMajority),
                ApprovalDraft("ag1", "Confirm"),
            },
        };

        var errors = BatchDefinitionClientValidator.Validate(model);

        errors.Should().BeEmpty();
    }

    // ── Nested ParallelGroup rejection (server + client both forbid it) ──────────

    [Fact]
    public void Validate_NestedParallelGroup_FlagsNestedStepType()
    {
        // A v0.1 invariant — single-level only. A wizard could in theory drop a Parallel as a child,
        // but Wizard.razor's UI only exposes Job-child editors. This locks the validator side.
        var inner = new WizardStepDraft
        {
            StepId = "inner",
            StepType = BatchStepType.ParallelGroup,
            Children = { JobDraft("ic1", "X"), JobDraft("ic2", "Y") },
        };
        var outer = new WizardStepDraft
        {
            StepId = "outer",
            StepType = BatchStepType.ParallelGroup,
            Children = { inner, JobDraft("oc1", "Z") },
        };
        var model = new BatchWizardModel { Name = "ok", Steps = { outer } };

        var errors = BatchDefinitionClientValidator.Validate(model);

        errors.Should().ContainKey("Steps[0].ParallelGroup.Steps[0].StepType");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static WizardStepDraft JobDraft(string id, string jobName = "Echo") => new()
    {
        StepId = id,
        StepType = BatchStepType.Job,
        JobName = jobName,
    };

    private static WizardStepDraft ParallelDraft(string id, IEnumerable<WizardStepDraft> children,
        ParallelJoinPolicy join = ParallelJoinPolicy.WaitAll) => new()
    {
        StepId = id,
        StepType = BatchStepType.ParallelGroup,
        JoinPolicy = join,
        Children = children.ToList(),
    };

    private static WizardStepDraft ApprovalDraft(string id, string title) => new()
    {
        StepId = id,
        StepType = BatchStepType.ApprovalGate,
        ApprovalTitle = title,
        AllowedRoles = { "ops" },
        OnTimeout = ApprovalTimeoutAction.Fail,
    };

    /// <summary>
    /// filter out server-only paths the wizard can't reach so the parity assertion stays honest.
    /// The wizard always emits non-null Job/ParallelGroup/Approval payloads (via WizardStepDraft union),
    /// always picks enum values from dropdowns (Enum.IsDefined holds), and never sets Id (server assigns).
    /// </summary>
    private static bool IsWizardEmittablePath(string path)
    {
        if (path == "Id") return false;
        if (path == "FailurePolicy") return false; // dropdown → always Enum.IsDefined
        // Server-emitted.Job /.ParallelGroup /.Approval null-payload paths (wizard always emits payloads).
        if (path.EndsWith(".Job", StringComparison.Ordinal) ||
            path.EndsWith(".ParallelGroup", StringComparison.Ordinal) ||
            path.EndsWith(".Approval", StringComparison.Ordinal))
        {
            return false;
        }
        // Server JoinPolicy Enum.IsDefined (dropdown).
        if (path.EndsWith(".ParallelGroup.JoinPolicy", StringComparison.Ordinal)) return false;
        return true;
    }

    // Local minimal envelope (ValidationProblemDetails-compatible) so we don't pull a heavy DTO.
    private sealed class ValidationProblemEnvelope
    {
        public string? Type { get; set; }
        public string? Title { get; set; }
        public int? Status { get; set; }
        public Dictionary<string, string[]>? Errors { get; set; }
    }
}
