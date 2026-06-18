using System.Net;
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
using UKBatch.Dashboard.Models.Wizard;
using UKBatch.Dashboard.Tests.Pages.Common;
using Xunit;

namespace UKBatch.Dashboard.Tests.Components;

/// <summary>
/// bunit tests for the Create/Edit Wizard
/// (<see cref="UKBatch.Dashboard.Components.Pages.Batches.Wizard"/>).
/// Covers navigation gating, add/remove/reorder, server-error step jumps, and edit-mode load.
/// </summary>
public sealed class WizardTests : TestContext
{
    public WizardTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose; // DagView preview inside Review step imports the JS module.
    }

    // ── shared scaffolding ────────────────────────────────────────────────────────

    private const string Svc = "svc";

    private (IUKBatchClient client, IUKBatchClientFactory factory) WireDeps(
        IReadOnlyList<JobDefinitionDto>? jobs = null)
    {
        var registry = PageTestHelpers.RegistryWith(PageTestHelpers.Descriptor(Svc));
        var client = PageTestHelpers.BuildClient();
        client.ListJobsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>())
            .Returns(new PageEnvelope<JobDefinitionDto>
            {
                Items = jobs ?? new[]
                {
                    new JobDefinitionDto
                    {
                        Name = "JobA",
                        IsPartitioned = false,
                        MaxRetries = 0,
                        TimeoutSeconds = 0,
                        DefaultParameters = new Dictionary<string, object?>(),
                        Tags = Array.Empty<string>(),
                    },
                    new JobDefinitionDto
                    {
                        Name = "JobB",
                        IsPartitioned = false,
                        MaxRetries = 0,
                        TimeoutSeconds = 0,
                        DefaultParameters = new Dictionary<string, object?>(),
                        Tags = Array.Empty<string>(),
                    },
                },
                TotalCount = 2,
                Offset = 0,
                Limit = 500,
            });
        var factory = PageTestHelpers.FactoryFor(Svc, client);
        Services.AddSingleton(registry);
        Services.AddSingleton(factory);
        Services.AddSingleton(PageTestHelpers.NewState());
        Services.AddSingleton(PageTestHelpers.NewNotifications());
        return (client, factory);
    }

    private IRenderedComponent<Wizard> RenderCreate()
    {
        return RenderComponent<Wizard>(p => p
            .Add(w => w.ServiceName, Svc)
            .Add(w => w.BatchId, (string?)null));
    }

    private IRenderedComponent<Wizard> RenderEdit(string batchId)
    {
        return RenderComponent<Wizard>(p => p
            .Add(w => w.ServiceName, Svc)
            .Add(w => w.BatchId, batchId));
    }

    private static BatchDefinitionDto BuildExistingDef(string id, string name, int version, BatchSource source = BatchSource.Dashboard)
    {
        return new BatchDefinitionDto
        {
            Id = id,
            Name = name,
            Source = source,
            Version = version,
            Steps = new[]
            {
                new BatchStep
                {
                    StepId = "s1", Order = 0, StepType = BatchStepType.Job,
                    Job = new JobStepData { JobName = "JobA" },
                },
            },
            FailurePolicy = BatchFailurePolicy.StopOnFailure,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    // ── forward gating (Next disabled until Definition.Name set) ───────────

    [Fact]
    public void Navigation_ForwardGatedByValidation_NextDisabledUntilNameSet()
    {
        WireDeps();
        var cut = RenderCreate();

        cut.WaitForState(() => cut.FindAll("button").Count > 0);

        // Find the "Next" button (text starts with "Next").
        var next = cut.FindAll("button").FirstOrDefault(b => b.TextContent.Contains("Next", StringComparison.OrdinalIgnoreCase));
        next.Should().NotBeNull("the Definition step renders a Next button");
        next!.HasAttribute("disabled").Should().BeTrue(
            "Next MUST be disabled while Definition.Name is blank (per-step forward gating)");

        // Set the name input; Next must enable. `@bind:event="oninput"` ⇒ use Input not Change.
        var nameInput = cut.Find("input#batch-name");
        nameInput.Input("my-batch");

        var nextAfter = cut.FindAll("button").FirstOrDefault(b => b.TextContent.Contains("Next", StringComparison.OrdinalIgnoreCase));
        nextAfter!.HasAttribute("disabled").Should().BeFalse(
            "after Name is set, Next must become enabled");

        // Stepper class on the Definition step must be --current.
        cut.FindAll("button.wizard-stepper__step--current").Should().NotBeEmpty(
            "the active step bears the --current modifier");
    }

    // ── ParallelGroup WaitMajority < 3 children shows error + blocks Next ──

    [Fact]
    public void ParallelGroup_WaitMajorityUnder3Children_ShowsErrorAndBlocksNext()
    {
        WireDeps();
        var cut = RenderCreate();
        cut.WaitForState(() => cut.Find("input#batch-name") is not null);

        // Step 1 — set Name, then advance to Steps.
        cut.Find("input#batch-name").Input("ok");
        ClickByText(cut, "Next");

        // Step 2 — add a Parallel group (defaults to 2 children).
        ClickByAriaLabelOrText(cut, "Parallel group");
        cut.WaitForState(() => cut.FindAll("select.form-field__select").Any());

        // Toggle the JoinPolicy <select> to WaitMajority.
        var joinSelect = cut.FindAll("select.form-field__select")
            .FirstOrDefault(s => s.InnerHtml.Contains("WaitAll", StringComparison.Ordinal)
                              && s.InnerHtml.Contains("WaitMajority", StringComparison.Ordinal));
        joinSelect.Should().NotBeNull("ParallelGroup editor renders a JoinPolicy select");
        joinSelect!.Change(ParallelJoinPolicy.WaitMajority.ToString());

        cut.Markup.Should().Contain("WaitMajority requires",
            "the wizard mirrors the validator: WaitMajority with <3 children flags an inline error");

        // Next is disabled (validator-failing slice for the Steps step).
        var next = cut.FindAll("button").FirstOrDefault(b => b.TextContent.Contains("Next", StringComparison.OrdinalIgnoreCase));
        next!.HasAttribute("disabled").Should().BeTrue();
    }

    // ── parallel-child mutation re-evaluates the group's inline error ───

    [Fact]
    public void ParallelGroup_AddBranchToWaitMajority_ClearsInlineError()
    {
        // Locks the riskiest StepDraftEditor extraction seam: a child
        // mutation inside the extracted editor must bubble (AddChildAsync → OnChanged → Wizard
        // re-render) so the PARENT's group-level inline error re-evaluates. WaitMajority seeds 2
        // children → "WaitMajority requires >=3 children" shows; adding ONE branch (→3) must clear it.
        //
        // APPROACH: driven through the FULL Wizard (not a standalone StepDraftEditor) — this is the
        // higher-value path because it exercises the real extraction seam end-to-end (the child's
        // OnChanged re-rendering the parent Wizard, which owns the inline-error markup). The existing
        // (ParallelGroup_WaitMajorityUnder3Children…) already proves the nav + JoinPolicy-select
        // DOM is reachable, so the same harness drives this reliably.
        WireDeps();
        var cut = RenderCreate();
        cut.WaitForState(() => cut.Find("input#batch-name") is not null);

        // Step 1 — name, advance to Steps.
        cut.Find("input#batch-name").Input("ok");
        ClickByText(cut, "Next");

        // Step 2 — add a Parallel group (auto-expands; seeds 2 children).
        ClickByAriaLabelOrText(cut, "Parallel group");
        cut.WaitForState(() => cut.FindAll("select.form-field__select").Any());

        // Switch JoinPolicy → WaitMajority: the 2-child error appears.
        var joinSelect = cut.FindAll("select.form-field__select")
            .First(s => s.InnerHtml.Contains("WaitAll", StringComparison.Ordinal)
                     && s.InnerHtml.Contains("WaitMajority", StringComparison.Ordinal));
        joinSelect.Change(ParallelJoinPolicy.WaitMajority.ToString());
        cut.Markup.Should().Contain("WaitMajority requires",
            "WaitMajority with 2 children flags the inline error (precondition for this test)");

        // Click "Add branch" ONCE (→ 3 children). This goes through StepDraftEditor.AddChildAsync,
        // which ends with `await OnChanged.InvokeAsync` — the seam under test.
        var addBranch = cut.FindAll("button")
            .First(b => b.TextContent.Contains("Add branch", StringComparison.OrdinalIgnoreCase));
        addBranch.Click();

        // The group now has 3 children → the WaitMajority degeneracy error must be gone.
        cut.WaitForState(() => !cut.Markup.Contains("WaitMajority requires", StringComparison.Ordinal));
        cut.Markup.Should().NotContain("WaitMajority requires",
            "adding a 3rd branch must clear the WaitMajority error — proving the extracted child's " +
            "OnChanged bubbled up and the parent re-evaluated its group-level validation");
    }

    // ── Add / Remove / Reorder steps ──────────────────────────────────────

    [Fact]
    public void AddRemoveReorderSteps_MutatesModelInOrder()
    {
        WireDeps();
        var cut = RenderCreate();
        cut.WaitForState(() => cut.Find("input#batch-name") is not null);

        // Step 1 — set Name then go to Steps.
        cut.Find("input#batch-name").Input("ok");
        ClickByText(cut, "Next");

        cut.WaitForState(() => cut.FindAll("button").Any(b => b.TextContent.Contains("Job", StringComparison.OrdinalIgnoreCase)
                                                            && !b.TextContent.Contains("policy", StringComparison.OrdinalIgnoreCase)));

        // Click "Job" add button TWICE: should produce 2 step rows.
        var jobAddButton = cut.FindAll("button.btn--secondary.btn--sm")
            .First(b => b.TextContent.Contains("Job", StringComparison.OrdinalIgnoreCase)
                     && !b.TextContent.Contains("Parallel", StringComparison.OrdinalIgnoreCase)
                     && !b.TextContent.Contains("Approval", StringComparison.OrdinalIgnoreCase));
        jobAddButton.Click();
        cut.WaitForState(() => cut.FindAll("div.wizard-step-row").Count >= 1);
        jobAddButton = cut.FindAll("button.btn--secondary.btn--sm")
            .First(b => b.TextContent.Contains("Job", StringComparison.OrdinalIgnoreCase)
                     && !b.TextContent.Contains("Parallel", StringComparison.OrdinalIgnoreCase)
                     && !b.TextContent.Contains("Approval", StringComparison.OrdinalIgnoreCase));
        jobAddButton.Click();
        cut.WaitForState(() => cut.FindAll("div.wizard-step-row").Count >= 2);

        // Reorder: click "Move down" on the FIRST row's controls.
        var moveDown = cut.FindAll("button[aria-label='Move down']").First();
        moveDown.HasAttribute("disabled").Should().BeFalse(
            "with 2 rows, the first row's Move-down must be enabled");
        moveDown.Click();
        // After swap, the first row's Move-down should still be enabled (now points at the old 1st, now at idx 1).
        // We just assert the row count is unchanged.
        cut.FindAll("div.wizard-step-row").Count.Should().Be(2);

        // Delete the first row.
        var del = cut.FindAll("button[aria-label='Delete']").First();
        del.Click();
        cut.WaitForState(() => cut.FindAll("div.wizard-step-row").Count == 1);
    }

    // ── Submit Create calls client + navigates to Detail ──────────────────

    [Fact]
    public async Task Submit_Create_CallsClientAndNavigates()
    {
        var (client, _) = WireDeps();

        var createdDto = new BatchDefinitionDto
        {
            Id = "new-id-XYZ",
            Name = "my-batch",
            Source = BatchSource.Dashboard,
            Version = 1,
            Steps = new[]
            {
                new BatchStep
                {
                    StepId = "s1", Order = 0, StepType = BatchStepType.Job,
                    Job = new JobStepData { JobName = "JobA" },
                },
            },
            FailurePolicy = BatchFailurePolicy.StopOnFailure,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        CreateBatchRequest? captured = null;
        client.CreateBatchAsync(Arg.Do<CreateBatchRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(createdDto);

        var cut = RenderCreate();
        cut.WaitForState(() => cut.Find("input#batch-name") is not null);

        // Step 1 — name.
        cut.Find("input#batch-name").Input("my-batch");
        ClickByText(cut, "Next");

        // Step 2 — add a Job step and pick "JobA".
        cut.WaitForState(() => cut.FindAll("button.btn--secondary.btn--sm").Any());
        var jobAdd = cut.FindAll("button.btn--secondary.btn--sm")
            .First(b => b.TextContent.Contains("Job", StringComparison.OrdinalIgnoreCase)
                     && !b.TextContent.Contains("Parallel", StringComparison.OrdinalIgnoreCase)
                     && !b.TextContent.Contains("Approval", StringComparison.OrdinalIgnoreCase));
        jobAdd.Click();
        cut.WaitForState(() => cut.FindAll("select.form-field__select").Any());
        SelectJobFromCatalog(cut, "JobA");

        // Go forward to Review (Steps → FailurePolicy → Schedule → Review).
        ClickByText(cut, "Next");
        ClickByText(cut, "Next");
        ClickByText(cut, "Next");

        cut.WaitForState(() => cut.FindAll("button").Any(b => b.TextContent.Contains("Create batch", StringComparison.OrdinalIgnoreCase)));
        var submit = cut.FindAll("button").First(b => b.TextContent.Contains("Create batch", StringComparison.OrdinalIgnoreCase));
        await submit.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        // Client received a Create request with the right shape.
        await client.Received(1).CreateBatchAsync(Arg.Any<CreateBatchRequest>(), Arg.Any<CancellationToken>());
        captured.Should().NotBeNull();
        captured!.Name.Should().Be("my-batch");
        captured.Source.Should().Be(BatchSource.Dashboard);
        captured.Steps.Should().ContainSingle();

        // bunit's FakeNavigationManager records NavigateTo — Wizard navigates to /dashboard/{svc}/batches/{id}.
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.Uri.Should().Contain($"/batches/{createdDto.Id}",
            "successful Create must navigate to the Detail page for the new id");
    }

    // ── Edit-mode load carries Version into Update ───────────────────────

    [Fact]
    public async Task EditMode_LoadsDefinition_CarriesVersionIntoUpdateRequest()
    {
        var (client, _) = WireDeps();
        var existing = BuildExistingDef("existing-id", "old-name", version: 5);
        client.GetBatchByIdAsync("existing-id", Arg.Any<CancellationToken>())
            .Returns(existing);
        UpdateBatchRequest? capturedUpdate = null;
        client.UpdateBatchAsync(
                Arg.Any<string>(),
                Arg.Do<UpdateBatchRequest>(r => capturedUpdate = r),
                Arg.Any<CancellationToken>())
            .Returns(existing with { Version = 6 });

        var cut = RenderEdit("existing-id");
        cut.WaitForState(() => cut.Find("input#batch-name") is not null);

        // The loaded name is in the input.
        cut.Find("input#batch-name").GetAttribute("value").Should().Be("old-name");

        // Navigate Definition → Steps → FailurePolicy → Schedule → Review.
        ClickByText(cut, "Next");
        ClickByText(cut, "Next");
        ClickByText(cut, "Next");
        ClickByText(cut, "Next");

        cut.WaitForState(() => cut.FindAll("button").Any(b => b.TextContent.Contains("Save changes", StringComparison.OrdinalIgnoreCase)));
        var submit = cut.FindAll("button").First(b => b.TextContent.Contains("Save changes", StringComparison.OrdinalIgnoreCase));
        await submit.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        await client.Received(1).UpdateBatchAsync(
            "existing-id",
            Arg.Any<UpdateBatchRequest>(),
            Arg.Any<CancellationToken>());
        capturedUpdate.Should().NotBeNull();
        capturedUpdate!.Id.Should().Be("existing-id");
        capturedUpdate.Version.Should().Be(5, "F-5: edit MUST carry the loaded Version for optimistic concurrency");
    }

    // ── server 400 with Steps[i].… path jumps to Steps + paints error ────

    [Fact]
    public async Task ServerValidation400_OnStepsPath_JumpsToStepsAndPaintsError()
    {
        var (client, _) = WireDeps();
        var validationErrors = new Dictionary<string, string[]>
        {
            ["Steps[0].Job.JobName"] = new[] { "must be non-empty" },
        };
        client.CreateBatchAsync(Arg.Any<CreateBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns<BatchDefinitionDto>(_ => throw new UKBatchClientException(
                "Validation failed",
                HttpStatusCode.BadRequest,
                problemType: "https://tools.ietf.org/html/rfc9110#section-15.5.1",
                detail: null,
                validationErrors: validationErrors));

        var cut = RenderCreate();
        cut.WaitForState(() => cut.Find("input#batch-name") is not null);

        // Step 1: name.
        cut.Find("input#batch-name").Input("my-batch");
        ClickByText(cut, "Next");

        // Step 2 — add a Job step with a selected job (so we PASS local validation, server is the test).
        cut.WaitForState(() => cut.FindAll("button.btn--secondary.btn--sm").Any());
        var jobAdd = cut.FindAll("button.btn--secondary.btn--sm")
            .First(b => b.TextContent.Contains("Job", StringComparison.OrdinalIgnoreCase)
                     && !b.TextContent.Contains("Parallel", StringComparison.OrdinalIgnoreCase)
                     && !b.TextContent.Contains("Approval", StringComparison.OrdinalIgnoreCase));
        jobAdd.Click();
        cut.WaitForState(() => cut.FindAll("select.form-field__select").Any());
        SelectJobFromCatalog(cut, "JobA");

        // Step 2 → 3 → 4 → 5 (Review) → Submit (the stubbed client throws).
        ClickByText(cut, "Next");
        ClickByText(cut, "Next");
        ClickByText(cut, "Next");
        cut.WaitForState(() => cut.FindAll("button").Any(b => b.TextContent.Contains("Create batch", StringComparison.OrdinalIgnoreCase)));
        var submit = cut.FindAll("button").First(b => b.TextContent.Contains("Create batch", StringComparison.OrdinalIgnoreCase));
        await submit.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        // After Submit → 400, wizard MUST jump back to the Steps step (path discriminator `Steps[*]`).
        cut.WaitForState(() => cut.FindAll("button.wizard-stepper__step--current").Any());
        var currentLabel = cut.FindAll("button.wizard-stepper__step--current").First().TextContent;
        currentLabel.Should().Contain("Steps",
 "a server 400 on `Steps[0].Job.JobName` MUST navigate back to the Steps wizard step");

        // The client received exactly one CreateBatchAsync attempt (no auto-retry on validation failure).
        await client.Received(1).CreateBatchAsync(Arg.Any<CreateBatchRequest>(), Arg.Any<CancellationToken>());
    }

    // ── BatchWizardModel.Source preserved through edit ───────────────

    [Fact]
    public async Task EditMode_ApiSourceBatch_SourcePreservedInUpdateRequest()
    {
        // lock: a batch loaded with Source=Api MUST round-trip as Source=Api (not silently
        // flipped to Dashboard). The wizard VM captures the source via FromDefinition.
        var (client, _) = WireDeps();
        var apiSourced = BuildExistingDef("api-id", "api-batch", version: 2, source: BatchSource.Api);
        client.GetBatchByIdAsync("api-id", Arg.Any<CancellationToken>())
            .Returns(apiSourced);
        UpdateBatchRequest? captured = null;
        client.UpdateBatchAsync(
                Arg.Any<string>(),
                Arg.Do<UpdateBatchRequest>(r => captured = r),
                Arg.Any<CancellationToken>())
            .Returns(apiSourced with { Version = 3 });

        var cut = RenderEdit("api-id");
        cut.WaitForState(() => cut.Find("input#batch-name") is not null);

        // Walk to Review and submit.
        ClickByText(cut, "Next");
        ClickByText(cut, "Next");
        ClickByText(cut, "Next");
        ClickByText(cut, "Next");
        cut.WaitForState(() => cut.FindAll("button").Any(b => b.TextContent.Contains("Save changes", StringComparison.OrdinalIgnoreCase)));
        var submit = cut.FindAll("button").First(b => b.TextContent.Contains("Save changes", StringComparison.OrdinalIgnoreCase));
        await submit.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        captured.Should().NotBeNull();
        captured!.Source.Should().Be(BatchSource.Api,
 "edit MUST preserve the loaded Source (Api→Api, not silently flipped to Dashboard)");
    }

    // ── Wizard guard: Code-source batches are read-only — redirected ──────

    [Fact]
    public void EditMode_CodeSource_RedirectsToDetail()
    {
        var (client, _) = WireDeps();
        var codeBatch = BuildExistingDef("code-id", "code-batch", version: 0, source: BatchSource.Code);
        client.GetBatchByIdAsync("code-id", Arg.Any<CancellationToken>())
            .Returns(codeBatch);

        var cut = RenderEdit("code-id");
        cut.WaitForState(() =>
        {
            var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
            return nav.Uri.Contains("/batches/code-id", StringComparison.Ordinal);
        });

        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.Uri.Should().Contain($"/dashboard/{Svc}/batches/code-id",
 "Code-source batches redirect from Edit to the read-only Detail page");
        nav.Uri.Should().NotContain("/edit",
            "redirect target is Detail, not the Edit route");
    }

    // ── server 400 on OnFailureSteps[i] jumps to FailurePolicy ───

    [Fact]
    public async Task ServerValidation400_OnFailureStepsPath_JumpsToFailurePolicyStep()
    {
        var (client, _) = WireDeps();
        var validationErrors = new Dictionary<string, string[]>
        {
            ["OnFailureSteps[0].Job.JobName"] = new[] { "must be non-empty" },
        };
        client.CreateBatchAsync(Arg.Any<CreateBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns<BatchDefinitionDto>(_ => throw new UKBatchClientException(
                "Validation failed",
                HttpStatusCode.BadRequest,
                problemType: "https://tools.ietf.org/html/rfc9110#section-15.5.1",
                detail: null,
                validationErrors: validationErrors));

        var cut = RenderCreate();
        cut.WaitForState(() => cut.Find("input#batch-name") is not null);

        // Build a minimal valid model and submit.
        cut.Find("input#batch-name").Input("my-batch");
        ClickByText(cut, "Next");
        cut.WaitForState(() => cut.FindAll("button.btn--secondary.btn--sm").Any());
        var jobAdd = cut.FindAll("button.btn--secondary.btn--sm")
            .First(b => b.TextContent.Contains("Job", StringComparison.OrdinalIgnoreCase)
                     && !b.TextContent.Contains("Parallel", StringComparison.OrdinalIgnoreCase)
                     && !b.TextContent.Contains("Approval", StringComparison.OrdinalIgnoreCase));
        jobAdd.Click();
        cut.WaitForState(() => cut.FindAll("select.form-field__select").Any());
        SelectJobFromCatalog(cut, "JobA");

        ClickByText(cut, "Next");
        ClickByText(cut, "Next");
        ClickByText(cut, "Next");
        cut.WaitForState(() => cut.FindAll("button").Any(b => b.TextContent.Contains("Create batch", StringComparison.OrdinalIgnoreCase)));
        var submit = cut.FindAll("button").First(b => b.TextContent.Contains("Create batch", StringComparison.OrdinalIgnoreCase));
        await submit.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        cut.WaitForState(() => cut.FindAll("button.wizard-stepper__step--current").Any());
        var currentLabel = cut.FindAll("button.wizard-stepper__step--current").First().TextContent;
        currentLabel.Should().Contain("Failure",
 "a 400 on OnFailureSteps[i] path MUST jump to the FailurePolicy wizard step");
    }

    // ── schedule catch-up window renders on the Schedule step + flows into the create request ──

    [Fact]
    public async Task ScheduleStep_CatchUpWindow_EntersAndFlowsIntoCreateRequest()
    {
        var (client, _) = WireDeps();
        var createdDto = new BatchDefinitionDto
        {
            Id = "cu-id",
            Name = "cu-batch",
            Source = BatchSource.Dashboard,
            Version = 1,
            Steps = new[]
            {
                new BatchStep
                {
                    StepId = "s1", Order = 0, StepType = BatchStepType.Job,
                    Job = new JobStepData { JobName = "JobA" },
                },
            },
            FailurePolicy = BatchFailurePolicy.StopOnFailure,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        CreateBatchRequest? captured = null;
        client.CreateBatchAsync(Arg.Do<CreateBatchRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(createdDto);

        var cut = RenderCreate();
        cut.WaitForState(() => cut.Find("input#batch-name") is not null);

        // Step 1 — name → Steps.
        cut.Find("input#batch-name").Input("cu-batch");
        ClickByText(cut, "Next");

        // Step 2 — add a Job step and pick JobA.
        cut.WaitForState(() => cut.FindAll("button.btn--secondary.btn--sm").Any());
        var jobAdd = cut.FindAll("button.btn--secondary.btn--sm")
            .First(b => b.TextContent.Contains("Job", StringComparison.OrdinalIgnoreCase)
                     && !b.TextContent.Contains("Parallel", StringComparison.OrdinalIgnoreCase)
                     && !b.TextContent.Contains("Approval", StringComparison.OrdinalIgnoreCase));
        jobAdd.Click();
        cut.WaitForState(() => cut.FindAll("select.form-field__select").Any());
        SelectJobFromCatalog(cut, "JobA");

        // Steps → FailurePolicy → Schedule.
        ClickByText(cut, "Next");
        ClickByText(cut, "Next");

        // The catch-up field renders on the Schedule step.
        cut.WaitForState(() => cut.FindAll("input#catchup-value").Any());
        var catchUp = cut.Find("input#catchup-value");
        catchUp.HasAttribute("disabled").Should().BeTrue(
            "the catch-up field is disabled until a cron schedule is entered");

        // Enter a cron expression — the catch-up field must enable.
        var cron = cut.FindAll("input.form-field__input.mono").First();
        cron.Input("0 0 * * * *");
        cut.WaitForState(() => !cut.Find("input#catchup-value").HasAttribute("disabled"));

        // Enter the catch-up magnitude and pick the Hours unit. `@onchange` ⇒ Change, not Input.
        cut.Find("input#catchup-value").Change("6");
        var unitSelect = cut.FindAll("select.form-field__select")
            .First(s => s.InnerHtml.Contains("Minutes", StringComparison.Ordinal)
                     && s.InnerHtml.Contains("Hours", StringComparison.Ordinal));
        unitSelect.Change(CatchUpWindowUnit.Hours.ToString());

        // Schedule → Review → Submit.
        ClickByText(cut, "Next");
        cut.WaitForState(() => cut.FindAll("button").Any(b => b.TextContent.Contains("Create batch", StringComparison.OrdinalIgnoreCase)));
        var submit = cut.FindAll("button").First(b => b.TextContent.Contains("Create batch", StringComparison.OrdinalIgnoreCase));
        await submit.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        captured.Should().NotBeNull();
        captured!.Schedule.Should().Be("0 0 * * * *");
        captured.ScheduleCatchUpWindow.Should().Be(TimeSpan.FromHours(6),
            "the entered 6-hour catch-up window must flow through the Schedule step into the create request");
    }

    // ── edit-mode load round-trips an existing catch-up window into the field ──

    [Fact]
    public void EditMode_LoadsExistingCatchUpWindow_PopulatesField()
    {
        var (client, _) = WireDeps();
        var existing = new BatchDefinitionDto
        {
            Id = "cu-edit",
            Name = "cu-edit-batch",
            Source = BatchSource.Dashboard,
            Version = 3,
            Schedule = "0 0 * * * *",
            ScheduleCatchUpWindow = TimeSpan.FromHours(6),
            Steps = new[]
            {
                new BatchStep
                {
                    StepId = "s1", Order = 0, StepType = BatchStepType.Job,
                    Job = new JobStepData { JobName = "JobA" },
                },
            },
            FailurePolicy = BatchFailurePolicy.StopOnFailure,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        client.GetBatchByIdAsync("cu-edit", Arg.Any<CancellationToken>()).Returns(existing);

        var cut = RenderEdit("cu-edit");
        cut.WaitForState(() => cut.Find("input#batch-name") is not null);

        // Walk to the Schedule step.
        ClickByText(cut, "Next");
        ClickByText(cut, "Next");
        ClickByText(cut, "Next");

        cut.WaitForState(() => cut.FindAll("input#catchup-value").Any());
        cut.Find("input#catchup-value").GetAttribute("value").Should().Be("6",
            "edit-load must round-trip a persisted 6-hour window into the magnitude field");
    }

    // ── duplicate parameter keys render the DAG preview without tearing down the circuit ──

    [Fact]
    public void ReviewPreview_DuplicateParameterKeys_RendersWithoutThrowing()
    {
        // The Review step renders `<DagView Steps="@_model.StepsAsBatchSteps()" ...>`, so the draft→step
        // projection runs DURING render. The parameter editor seeds new rows with an empty key, so two
        // rows can share a key (blank or otherwise); the projection used to throw ArgumentException from
        // the dictionary build — on the render path that tears down the Blazor circuit and loses the
        // unsaved batch. This drives the exact Review wiring (projection feeding DagView) with a
        // duplicate-key model and asserts it renders.
        var model = new BatchWizardModel
        {
            Name = "dup-params",
            Steps =
            {
                new WizardStepDraft
                {
                    StepId = "s1",
                    StepType = BatchStepType.Job,
                    JobName = "JobA",
                    Parameters =
                    {
                        new KeyValuePair<string, string>("dup", "first"),
                        new KeyValuePair<string, string>("dup", "second"),
                        new KeyValuePair<string, string>(string.Empty, string.Empty),
                    },
                },
            },
        };

        var render = () => RenderComponent<UKBatch.Dashboard.Components.Shared.DagView>(p => p
            .Add(d => d.Steps, model.StepsAsBatchSteps())
            .Add(d => d.OnFailureSteps, model.OnFailureAsBatchSteps()));

        render.Should().NotThrow(
            "the Review preview projects drafts to steps during render — a throw here tears down the circuit");
        var cut = render();
        cut.FindAll("foreignObject").Should().ContainSingle(
            "the single Job step renders one DAG node despite the duplicate/blank parameter keys");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static void ClickByText(IRenderedComponent<Wizard> cut, string textFragment)
    {
        var btn = cut.FindAll("button")
            .FirstOrDefault(b => b.TextContent.Contains(textFragment, StringComparison.OrdinalIgnoreCase)
                              && !b.HasAttribute("disabled"));
        btn.Should().NotBeNull($"a button with text '{textFragment}' must be present and enabled");
        btn!.Click();
    }

    private static void ClickByAriaLabelOrText(IRenderedComponent<Wizard> cut, string label)
    {
        var btn = cut.FindAll("button")
            .FirstOrDefault(b => b.TextContent.Contains(label, StringComparison.OrdinalIgnoreCase));
        btn.Should().NotBeNull();
        btn!.Click();
    }

    // the Job-name dropdown is now a CATALOG select — its option VALUES are opaque index
    // tokens (not the job name), because the same job can exist on multiple services. Selecting a job
    // means changing the select to the VALUE of the <option> whose label starts with the job name.
    private static void SelectJobFromCatalog(IRenderedComponent<Wizard> cut, string jobName)
    {
        var jobSelect = cut.FindAll("select.form-field__select")
            .First(s => s.InnerHtml.Contains("— select a job —", StringComparison.Ordinal));
        var option = jobSelect.QuerySelectorAll("option")
            .First(o => o.TextContent.TrimStart().StartsWith(jobName, StringComparison.Ordinal));
        jobSelect.Change(option.GetAttribute("value"));
    }
}
