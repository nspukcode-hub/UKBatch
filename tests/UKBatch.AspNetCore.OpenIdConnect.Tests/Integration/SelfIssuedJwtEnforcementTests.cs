using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using UKBatch.Abstractions.Batches;
using UKBatch.Api.Batches;
using Xunit;

namespace UKBatch.AspNetCore.OpenIdConnect.Tests.Integration;

/// <summary>
/// End-to-end enforcement proof over the REAL role-gated <c>/api</c> surface with self-issued JWTs.
/// The load-bearing case is <see cref="Viewer_TriggerJob_Forbidden"/>: the trigger endpoint MUST be
/// operator-gated, so a read-only viewer (or any non-operator) cannot dispatch work. If that assertion
/// ever fails, role-gating has a privilege-escalation hole.
/// </summary>
public sealed class SelfIssuedJwtEnforcementTests : IClassFixture<RoleGatedApiHostFixture>
{
    private readonly RoleGatedApiHostFixture _host;

    public SelfIssuedJwtEnforcementTests(RoleGatedApiHostFixture host) => _host = host;

    private static string ViewerToken() =>
        RoleGatedApiHostFixture.TokenWithFlatRoles("vera", RoleGatedApiHostFixture.ViewerRole);

    private static string OperatorToken() =>
        RoleGatedApiHostFixture.TokenWithFlatRoles("olga", RoleGatedApiHostFixture.OperatorRole);

    private static HttpContent EmptyJson() =>
        new StringContent("{}", Encoding.UTF8, "application/json");

    // ---- Viewer (read-only) --------------------------------------------------------------------

    [Fact]
    public async Task Viewer_GetJobs_Ok()
    {
        using var client = _host.CreateClient(ViewerToken());
        var resp = await client.GetAsync(new Uri("/api/jobs", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "reads are viewer-gated and a viewer is authenticated");
    }

    [Fact]
    public async Task Viewer_TriggerJob_Forbidden()
    {
        // The privilege-escalation guard: the trigger endpoint is classified Write, so the operator policy
        // gates it. A viewer must be rejected BEFORE the job is dispatched.
        using var client = _host.CreateClient(ViewerToken());
        var resp = await client.PostAsync(
            new Uri($"/api/jobs/{RoleGatedApiHostFixture.DemoJobName}/trigger", UriKind.Relative),
            EmptyJson());
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "triggering a job is a write; a viewer must not be able to dispatch work");
    }

    [Fact]
    public async Task Viewer_RunBatch_Forbidden()
    {
        using var client = _host.CreateClient(ViewerToken());
        var resp = await client.PostAsync(
            new Uri($"/api/batches/by-name/{RoleGatedApiHostFixture.PlainBatchName}/run", UriKind.Relative),
            EmptyJson());
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden, "running a batch is a write");
    }

    [Fact]
    public async Task Viewer_ExecutionsQuery_Ok()
    {
        // POST-but-read: the query endpoint is classified Read, so a viewer may call it.
        using var client = _host.CreateClient(ViewerToken());
        var resp = await client.PostAsync(new Uri("/api/executions/query", UriKind.Relative), EmptyJson());
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "executions/query is a read despite being a POST");
    }

    // ---- Operator (read + write) ---------------------------------------------------------------

    [Fact]
    public async Task Operator_TriggerJob_Accepted()
    {
        using var client = _host.CreateClient(OperatorToken());
        var resp = await client.PostAsync(
            new Uri($"/api/jobs/{RoleGatedApiHostFixture.DemoJobName}/trigger", UriKind.Relative),
            EmptyJson());
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted, "an operator may trigger a job");
    }

    [Fact]
    public async Task Operator_RunBatch_Accepted()
    {
        using var client = _host.CreateClient(OperatorToken());
        var resp = await client.PostAsync(
            new Uri($"/api/batches/by-name/{RoleGatedApiHostFixture.PlainBatchName}/run", UriKind.Relative),
            EmptyJson());
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted, "an operator may run a batch");
    }

    [Fact]
    public async Task Operator_CreateBatch_Created()
    {
        var body = new CreateBatchRequest
        {
            Name = "operator-created-" + Guid.NewGuid().ToString("N"),
            Source = BatchSource.Api,
            FailurePolicy = BatchFailurePolicy.StopOnFailure,
            Steps = new List<BatchStep>
            {
                new()
                {
                    StepId = "step-1",
                    Order = 0,
                    StepType = BatchStepType.Job,
                    Job = new JobStepData { JobName = RoleGatedApiHostFixture.DemoJobName },
                },
            },
        };

        using var client = _host.CreateClient(OperatorToken());
        var resp = await client.PostAsync(
            new Uri("/api/batches", UriKind.Relative),
            new StringContent(JsonSerializer.Serialize(body, RoleGatedApiHostFixture.Json), Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.Created, "an operator may create a batch");
    }

    // ---- Anonymous -----------------------------------------------------------------------------

    [Fact]
    public async Task Anonymous_GetJobs_Unauthorized()
    {
        using var client = _host.CreateClient();
        var resp = await client.GetAsync(new Uri("/api/jobs", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a read is gated and no token was presented");
    }

    [Fact]
    public async Task Anonymous_TriggerJob_Unauthorized()
    {
        using var client = _host.CreateClient();
        var resp = await client.PostAsync(
            new Uri($"/api/jobs/{RoleGatedApiHostFixture.DemoJobName}/trigger", UriKind.Relative),
            EmptyJson());
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a write is gated and no token was presented");
    }

    // ---- Nested-role token (flattening feeds the policy) ---------------------------------------

    [Fact]
    public async Task NestedRealmRoleToken_Operator_TriggerJob_Accepted()
    {
        // The token carries batch-operator ONLY under realm_access.roles (no flat role claim). The write
        // succeeds only because the flattening transformation projected it to a ClaimTypes.Role claim the
        // operator policy reads.
        var token = RoleGatedApiHostFixture.TokenWithNestedRealmRoles("nadia", RoleGatedApiHostFixture.OperatorRole);
        using var client = _host.CreateClient(token);
        var resp = await client.PostAsync(
            new Uri($"/api/jobs/{RoleGatedApiHostFixture.DemoJobName}/trigger", UriKind.Relative),
            EmptyJson());
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted,
            "nested realm roles must flatten to ClaimTypes.Role and satisfy the operator policy");
    }

    [Fact]
    public async Task NestedRealmRoleToken_Viewer_TriggerJob_Forbidden()
    {
        // Same nested shape, but only the viewer role — the operator policy must still reject the write.
        var token = RoleGatedApiHostFixture.TokenWithNestedRealmRoles("nils", RoleGatedApiHostFixture.ViewerRole);
        using var client = _host.CreateClient(token);
        var resp = await client.PostAsync(
            new Uri($"/api/jobs/{RoleGatedApiHostFixture.DemoJobName}/trigger", UriKind.Relative),
            EmptyJson());
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- GateDecision (authenticated, NOT operator-gated; Core allowed-roles is the authority) --

    [Fact]
    public async Task GateDecision_NonOperatorWithGateRole_Approves()
    {
        // finance-approver holds the gate role but NOT the operator role. If approve were operator-gated it
        // would 403 at the endpoint and never reach Core. A 204 proves it reached Core AND Core admitted.
        var financeToken = RoleGatedApiHostFixture.TokenWithFlatRoles("fatima", RoleGatedApiHostFixture.GateRole);
        var approvalId = await TriggerGatedAndGetApprovalIdAsync(financeToken);

        using var client = _host.CreateClient(financeToken);
        var resp = await client.PostAsync(
            new Uri($"/api/approvals/{approvalId}/approve", UriKind.Relative),
            new StringContent("{\"note\":\"ok\"}", Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "an authenticated non-operator holding the gate role reaches Core and Core admits");
    }

    [Fact]
    public async Task GateDecision_AuthenticatedWithoutGateRole_CoreForbids()
    {
        // A viewer is authenticated (reaches the endpoint — proving it is NOT operator-gated) but does not
        // hold the gate role, so the Core allowed-roles check rejects with 403.
        var viewerToken = ViewerToken();
        var approvalId = await TriggerGatedAndGetApprovalIdAsync(viewerToken);

        using var client = _host.CreateClient(viewerToken);
        var resp = await client.PostAsync(
            new Uri($"/api/approvals/{approvalId}/approve", UriKind.Relative),
            new StringContent("{\"note\":\"no\"}", Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the endpoint admitted the authenticated caller; Core's allowed-roles then rejected the mismatch");
    }

    // ---- Ingest (worker heartbeat is never gated) ----------------------------------------------

    [Fact]
    public async Task Ingest_WorkerBeat_ReachableWithoutToken_UnderRoleGating()
    {
        // The heartbeat is classified Ingest, so the role-gating convention never adds an authorize
        // requirement — an anonymous beat must still reach the handler (202), not be rejected.
        using var client = _host.CreateClient();
        var resp = await client.PostAsync(
            new Uri("/api/workers/beat", UriKind.Relative),
            new StringContent("{\"name\":\"worker-1\"}", Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted, "worker ingest stays anonymous under role-gating");
    }

    // ---- helpers -------------------------------------------------------------------------------

    private async Task<string> TriggerGatedAndGetApprovalIdAsync(string readerToken)
    {
        // Trigger the gated batch as an operator (running a batch is a write).
        using var operatorClient = _host.CreateClient(OperatorToken());
        var runResp = await operatorClient.PostAsync(
            new Uri($"/api/batches/by-name/{RoleGatedApiHostFixture.GatedBatchName}/run", UriKind.Relative),
            EmptyJson());
        runResp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var runJson = await runResp.Content.ReadAsStringAsync();
        using var runDoc = JsonDocument.Parse(runJson);
        var batchId = runDoc.RootElement.GetProperty("batchId").GetString()!;

        // Poll the (read-gated) approvals feed for this run's pending gate.
        using var readerClient = _host.CreateClient(readerToken);
        for (var i = 0; i < 200; i++)
        {
            var listResp = await readerClient.GetAsync(new Uri("/api/approvals", UriKind.Relative));
            if (listResp.IsSuccessStatusCode)
            {
                var body = await listResp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
                {
                    if (item.GetProperty("batchId").GetString() == batchId)
                    {
                        return item.GetProperty("approvalId").GetString()!;
                    }
                }
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"No pending approval surfaced for batch {batchId} within ~10s.");
    }
}
