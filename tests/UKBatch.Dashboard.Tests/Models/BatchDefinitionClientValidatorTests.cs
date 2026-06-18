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
        new object[]
        {
            "ApprovalGate AutoApprove with no timeout",
            new BatchWizardModel
            {
                Name = "ok",
                Steps = { ApprovalDraftTimeout("ag1", ApprovalTimeoutAction.AutoApprove, timeoutSeconds: null) },
            },
        },
        new object[]
        {
            "ApprovalGate Hold with no timeout",
            new BatchWizardModel
            {
                Name = "ok",
                Steps = { ApprovalDraftTimeout("ag1", ApprovalTimeoutAction.Hold, timeoutSeconds: null) },
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

    // ── OnFailureSteps coverage (the client surfaces blank fields up front so the operator
    // catches them before submit; the server validator also rejects them as a backstop) ─────────

    [Fact]
    public void Validate_OnFailureSteps_BlankJobName_ReportsPath()
    {
        // the wizard validator MUST report invalid OnFailureSteps (blank JobName etc.) up front so the
        // operator never ships a definition that would otherwise fail at runtime.
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

    // ── approval gate on-timeout / timeout consistency (client-only focused cases; the WAF
    // parity rows above prove the server agrees on the path) ─────────────────────────

    [Theory]
    [InlineData(ApprovalTimeoutAction.AutoApprove)]
    [InlineData(ApprovalTimeoutAction.Hold)]
    public void Validate_OnTimeoutNotFail_NoTimeout_ReportsTimeoutPath(ApprovalTimeoutAction onTimeout)
    {
        // AutoApprove/Hold with no duration leaves the gate waiting forever while the UI implies the
        // action fires — the wizard must surface it so the operator fixes the combination before submit.
        var model = new BatchWizardModel
        {
            Name = "ok",
            Steps = { ApprovalDraftTimeout("ag1", onTimeout, timeoutSeconds: null) },
        };

        var errors = BatchDefinitionClientValidator.Validate(model);

        errors.Should().ContainKey("Steps[0].Approval.Timeout",
            "an on-timeout action other than Fail requires a timeout duration");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Validate_OnTimeoutNotFail_NonPositiveTimeout_ReportsTimeoutPath(int timeoutSeconds)
    {
        // A zero/negative duration projects to a null TimeoutAfter (no timeout), so the same rule applies.
        var model = new BatchWizardModel
        {
            Name = "ok",
            Steps = { ApprovalDraftTimeout("ag1", ApprovalTimeoutAction.AutoApprove, timeoutSeconds) },
        };

        var errors = BatchDefinitionClientValidator.Validate(model);

        errors.Should().ContainKey("Steps[0].Approval.Timeout",
            "a non-positive timeout is treated as no timeout, so AutoApprove/Hold still needs a real duration");
    }

    [Fact]
    public void Validate_OnTimeoutFail_NoTimeout_IsValid()
    {
        // Fail + no timeout is a legitimate indefinite wait that only ends on a manual reject. The shared
        // ApprovalDraft() helper already uses this combination, so it MUST stay valid under the new rule.
        var model = new BatchWizardModel
        {
            Name = "ok",
            Steps = { ApprovalDraft("ag1", "Confirm") },
        };

        var errors = BatchDefinitionClientValidator.Validate(model);

        errors.Should().NotContainKey("Steps[0].Approval.Timeout",
            "Fail with no timeout is a valid indefinite wait");
    }

    [Theory]
    [InlineData(ApprovalTimeoutAction.AutoApprove)]
    [InlineData(ApprovalTimeoutAction.Hold)]
    [InlineData(ApprovalTimeoutAction.Fail)]
    public void Validate_OnTimeoutWithTimeout_IsValid(ApprovalTimeoutAction onTimeout)
    {
        // Any on-timeout action paired with a real duration is valid — the action has a time to fire.
        var model = new BatchWizardModel
        {
            Name = "ok",
            Steps = { ApprovalDraftTimeout("ag1", onTimeout, timeoutSeconds: 30) },
        };

        var errors = BatchDefinitionClientValidator.Validate(model);

        errors.Should().NotContainKey("Steps[0].Approval.Timeout",
            "a present timeout duration satisfies the consistency rule for every action");
    }

    // ── schedule catch-up window rules (client-only; the server does not reject a negative window
    // the same way — the model coerces non-positive to null — so the wizard surfaces it up front, like
    // the parameter-key rules below. These paths are excluded from the WAF parity matrix above) ─────

    [Fact]
    public void Validate_NegativeCatchUpWindow_FlagsWindowPath()
    {
        var model = new BatchWizardModel
        {
            Name = "ok",
            Schedule = "0 0 * * * *",
            CatchUpWindowValue = -5,
            Steps = { JobDraft("s1", "Echo") },
        };

        var errors = BatchDefinitionClientValidator.Validate(model);

        errors.Should().ContainKey("Schedule.CatchUpWindow",
            "a negative catch-up window can't express a real duration — surface it before submit");
    }

    [Fact]
    public void Validate_CatchUpWindowWithoutSchedule_FlagsNoEffect()
    {
        var model = new BatchWizardModel
        {
            Name = "ok",
            Schedule = null,
            CatchUpWindowValue = 10,
            Steps = { JobDraft("s1", "Echo") },
        };

        var errors = BatchDefinitionClientValidator.Validate(model);

        errors.Should().ContainKey("Schedule.CatchUpWindow",
            "a catch-up window with no schedule has no runtime effect — the wizard notes it");
    }

    [Fact]
    public void Validate_CatchUpWindowWithSchedule_IsValid()
    {
        var model = new BatchWizardModel
        {
            Name = "ok",
            Schedule = "0 0 * * * *",
            CatchUpWindowValue = 6,
            CatchUpWindowUnit = CatchUpWindowUnit.Hours,
            Steps = { JobDraft("s1", "Echo") },
        };

        var errors = BatchDefinitionClientValidator.Validate(model);

        errors.Should().NotContainKey("Schedule.CatchUpWindow",
            "a positive window paired with a schedule is the intended, valid configuration");
    }

    [Fact]
    public void Validate_NoCatchUpWindow_IsValid()
    {
        // The common default: a scheduled batch with no catch-up window must not raise a spurious error.
        var model = new BatchWizardModel
        {
            Name = "ok",
            Schedule = "0 0 * * * *",
            CatchUpWindowValue = null,
            Steps = { JobDraft("s1", "Echo") },
        };

        var errors = BatchDefinitionClientValidator.Validate(model);

        errors.Should().NotContainKey("Schedule.CatchUpWindow");
    }

    // ── parameter-key rules (client-only safety net; the server validator does
    // not inspect parameter keys, but the conversion silently drops/collapses them, so the
    // wizard tells the operator what would happen) ─────────────────────────────────

    [Fact]
    public void Validate_DuplicateParameterKeys_FlagsDuplicate()
    {
        // Two rows with the SAME non-blank key: the conversion is last-wins, so one value is silently
        // lost. The wizard flags it (Ordinal, matching the dictionary's StringComparer).
        var model = new BatchWizardModel
        {
            Name = "ok",
            Steps = { JobDraftWithParameters("s1", Param("dup", "a"), Param("dup", "b")) },
        };

        var errors = BatchDefinitionClientValidator.Validate(model);

        errors.Should().ContainKey("Steps[0].Job.Parameters[1].Key",
            "a duplicate non-blank parameter key must be surfaced (the conversion is last-wins)");
    }

    [Fact]
    public void Validate_BlankKeyWithValue_FlagsKeyRequired()
    {
        // A row with no key but a real value: the conversion drops it, so the value vanishes. Flag it.
        var model = new BatchWizardModel
        {
            Name = "ok",
            Steps = { JobDraftWithParameters("s1", Param("", "orphan")) },
        };

        var errors = BatchDefinitionClientValidator.Validate(model);

        errors.Should().ContainKey("Steps[0].Job.Parameters[0].Key",
            "a value with no key must be surfaced — the conversion would silently drop it");
    }

    [Fact]
    public void Validate_FullyBlankParameterRows_AreTolerated()
    {
        // Empty editor rows (key AND value blank) are just placeholders; the conversion drops them and
        // the validator stays silent.
        var model = new BatchWizardModel
        {
            Name = "ok",
            Steps = { JobDraftWithParameters("s1", Param("", ""), Param("", "")) },
        };

        var errors = BatchDefinitionClientValidator.Validate(model);

        errors.Should().NotContainKey("Steps[0].Job.Parameters[0].Key",
            "fully-blank rows are tolerated (just empty editor rows)");
        errors.Should().NotContainKey("Steps[0].Job.Parameters[1].Key");
    }

    [Fact]
    public void Validate_DistinctParameterKeys_ProduceNoParameterErrors()
    {
        // Inverse: valid distinct keys must not raise spurious errors.
        var model = new BatchWizardModel
        {
            Name = "ok",
            Steps = { JobDraftWithParameters("s1", Param("a", "1"), Param("b", "2")) },
        };

        var errors = BatchDefinitionClientValidator.Validate(model);

        errors.Keys.Should().NotContain(k => k.StartsWith("Steps[0].Job.Parameters", StringComparison.Ordinal),
            "distinct non-blank keys are valid");
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

    private static KeyValuePair<string, string> Param(string key, string value) => new(key, value);

    private static WizardStepDraft JobDraftWithParameters(string id, params KeyValuePair<string, string>[] pairs) => new()
    {
        StepId = id,
        StepType = BatchStepType.Job,
        JobName = "Echo",
        Parameters = pairs.ToList(),
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

    private static WizardStepDraft ApprovalDraftTimeout(string id, ApprovalTimeoutAction onTimeout, int? timeoutSeconds) => new()
    {
        StepId = id,
        StepType = BatchStepType.ApprovalGate,
        ApprovalTitle = "Confirm",   // non-blank so the only possible error is the timeout combination
        AllowedRoles = { "ops" },
        OnTimeout = onTimeout,
        TimeoutSecondsApproval = timeoutSeconds,
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
