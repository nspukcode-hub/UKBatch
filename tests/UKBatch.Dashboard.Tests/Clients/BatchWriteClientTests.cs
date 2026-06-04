using System.Net;
using FluentAssertions;
using UKBatch.Abstractions.Batches;
using UKBatch.Api.Batches;
using UKBatch.Api.Common;
using UKBatch.Dashboard.Clients;
using UKBatch.Dashboard.Tests.Common;
using Xunit;

namespace UKBatch.Dashboard.Tests.Clients;

/// <summary>
/// REST round-trip coverage for the three new write methods
/// (<c>CreateBatchAsync</c>, <c>UpdateBatchAsync</c>, <c>DeleteBatchAsync</c>) through the
/// Sample.RestApi WAF, plus the ProblemDetails → typed-exception mapping table.
/// </summary>
/// <remarks>
/// <para>Mirrors the existing <c>RestUKBatchClientRestTests</c> pattern — one
/// <see cref="SampleRestApiFactory"/> per class; each test builds its own <c>RestUKBatchClient</c>
/// via <see cref="RestUKBatchClientFactory.BuildRestOnly"/> so isolation is automatic.</para>
/// </remarks>
public sealed class BatchWriteClientTests : IClassFixture<SampleRestApiFactory>
{
    private readonly SampleRestApiFactory _factory;

    public BatchWriteClientTests(SampleRestApiFactory factory)
    {
        _factory = factory;
    }

    private static CreateBatchRequest BuildCreateRequest(string name) => new()
    {
        Name = name,
        Source = BatchSource.Dashboard,
        Steps = new[]
        {
            new BatchStep
            {
                StepId = "s1",
                Order = 0,
                StepType = BatchStepType.Job,
                Job = new JobStepData { JobName = "Sample.RestApi.Jobs.InvoiceGenerationJob" },
            },
        },
        FailurePolicy = BatchFailurePolicy.StopOnFailure,
    };

    private static string UniqueName(string prefix) =>
        $"{prefix}-{Guid.NewGuid():N}".Substring(0, Math.Min(48, prefix.Length + 33));

    // ── CreateBatch — persists + returns DTO with assigned id ──────────────

    [Fact]
    public async Task CreateBatch_Persists_ReturnsDtoWithAssignedId()
    {
        await using var client = RestUKBatchClientFactory.BuildRestOnly(_factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = BuildCreateRequest(UniqueName("t02-create"));

        var created = await client.CreateBatchAsync(request, cts.Token);

        created.Should().NotBeNull();
        created.Id.Should().NotBeNullOrEmpty("server assigns the id");
        created.Name.Should().Be(request.Name);
        created.Source.Should().Be(BatchSource.Dashboard);
        created.Version.Should().BeGreaterThanOrEqualTo(0,
            "freshly created definitions carry a server-assigned Version");

        // Round-trip via GetBatchByIdAsync proves persistence.
        var fetched = await client.GetBatchByIdAsync(created.Id, cts.Token);
        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be(created.Id);
        fetched.Name.Should().Be(request.Name);
    }

    // ── duplicate-name 409 → BatchDefinitionDuplicateName ProblemType ──────

    [Fact]
    public async Task CreateBatch_DuplicateName_Throws409DuplicateNameProblemType()
    {
        await using var client = RestUKBatchClientFactory.BuildRestOnly(_factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var name = UniqueName("t03-dup");

        await client.CreateBatchAsync(BuildCreateRequest(name), cts.Token);

        Func<Task> act = () => client.CreateBatchAsync(BuildCreateRequest(name), cts.Token);
        var ex = await act.Should().ThrowAsync<UKBatchClientException>();

        ex.Which.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ex.Which.ProblemType.Should().Be("ukbatch:batch-definition-duplicate-name",
            "duplicate Name on POST /batches MUST surface as the typed BatchDefinitionDuplicateName URI");
    }

    // ── Update stale Version 409 + Update unknown id 404 ───────────────────

    [Fact]
    public async Task UpdateBatch_StaleVersion_Throws409Concurrency()
    {
        await using var client = RestUKBatchClientFactory.BuildRestOnly(_factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var created = await client.CreateBatchAsync(BuildCreateRequest(UniqueName("t04-stale")), cts.Token);

        // First update advances Version to 1.
        var update1 = new UpdateBatchRequest
        {
            Id = created.Id,
            Name = created.Name,
            Source = BatchSource.Dashboard,
            Steps = created.Steps,
            FailurePolicy = created.FailurePolicy,
            Version = created.Version, // 0
        };
        var afterFirst = await client.UpdateBatchAsync(created.Id, update1, cts.Token);
        afterFirst.Version.Should().BeGreaterThan(created.Version);

        // Second update sent with the STALE Version (0) — must 409 with ConcurrencyConflict.
        var stale = new UpdateBatchRequest
        {
            Id = created.Id,
            Name = created.Name,
            Source = BatchSource.Dashboard,
            Steps = created.Steps,
            FailurePolicy = created.FailurePolicy,
            Version = created.Version, // STILL 0 — stale
        };
        Func<Task> act = () => client.UpdateBatchAsync(created.Id, stale, cts.Token);
        var ex = await act.Should().ThrowAsync<UKBatchClientException>();

        ex.Which.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ex.Which.ProblemType.Should().Be("ukbatch:concurrency-conflict",
            "stale Version on PUT MUST surface as ConcurrencyConflict (distinct from DuplicateName)");
    }

    [Fact]
    public async Task UpdateBatch_NotFound_Throws404BatchDefinitionNotFoundProblemType()
    {
        await using var client = RestUKBatchClientFactory.BuildRestOnly(_factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var unknownId = $"unknown-{Guid.NewGuid():N}";
        var update = new UpdateBatchRequest
        {
            Id = unknownId,
            Name = "doesnt-matter",
            Source = BatchSource.Dashboard,
            Steps = new[]
            {
                new BatchStep
                {
                    StepId = "s1", Order = 0, StepType = BatchStepType.Job,
                    Job = new JobStepData { JobName = "Sample.RestApi.Jobs.InvoiceGenerationJob" },
                },
            },
            FailurePolicy = BatchFailurePolicy.StopOnFailure,
            Version = 0,
        };

        Func<Task> act = () => client.UpdateBatchAsync(unknownId, update, cts.Token);
        var ex = await act.Should().ThrowAsync<UKBatchClientException>();

        ex.Which.StatusCode.Should().Be(HttpStatusCode.NotFound);
        ex.Which.ProblemType.Should().Be("ukbatch:batch-definition-not-found");
    }

    [Fact]
    public async Task UpdateBatch_RouteIdMismatchBodyId_ThrowsArgumentException()
    {
        // Defensive client-side guard (RestUKBatchClient impl asserts route id == body id).
        await using var client = RestUKBatchClientFactory.BuildRestOnly(_factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var body = new UpdateBatchRequest
        {
            Id = "real-id",
            Name = "x",
            Source = BatchSource.Dashboard,
            Steps = Array.Empty<BatchStep>(),
            FailurePolicy = BatchFailurePolicy.StopOnFailure,
            Version = 0,
        };

        Func<Task> act = () => client.UpdateBatchAsync("DIFFERENT-id", body, cts.Token);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ── Validation 400 + Delete idempotent + Delete code-source 400 ────────

    [Fact]
    public async Task CreateBatch_InvalidSteps_ThrowsValidationFailed_WithFieldErrors_AndRfcType()
    {
        // lock: the real `type` URI is the RFC 9110 URL (Results.ValidationProblem default).
        // The wizard discriminates on (StatusCode==400 && ValidationErrors is { Count: > 0 }), NOT
        // on `ProblemType == ValidationFailed`. This test exercises BOTH facets:
        // 1) ValidationErrors dict is populated with Steps[0].Job.JobName path.
        // 2) ProblemType IS the RFC URL (not ukbatch:validation-failed).
        await using var client = RestUKBatchClientFactory.BuildRestOnly(_factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Build a wizard-emittable invalid model: blank JobName on a Job step.
        var request = new CreateBatchRequest
        {
            Name = UniqueName("t05-validation"),
            Source = BatchSource.Dashboard,
            Steps = new[]
            {
                new BatchStep
                {
                    StepId = "s1", Order = 0, StepType = BatchStepType.Job,
                    Job = new JobStepData { JobName = string.Empty }, // INVALID
                },
            },
            FailurePolicy = BatchFailurePolicy.StopOnFailure,
        };

        Func<Task> act = () => client.CreateBatchAsync(request, cts.Token);
        var ex = await act.Should().ThrowAsync<UKBatchClientException>();

        ex.Which.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // the type URI is the RFC URL (Results.ValidationProblem default), NOT ukbatch:validation-failed.
        ex.Which.ProblemType.Should().Be("https://tools.ietf.org/html/rfc9110#section-15.5.1",
 "Results.ValidationProblem emits the RFC 9110 type URL. The wizard must NOT key on " +
            "ukbatch:validation-failed for the field-validation case.");
        // the errors dict MUST be populated with the Steps[i].Job.JobName path so the wizard
        // can map it to the owning step row.
        ex.Which.ValidationErrors.Should().NotBeNull();
        ex.Which.ValidationErrors!.Should().ContainKey("Steps[0].Job.JobName",
 "server emits per-path errors; wizard parses them to render form-field__error");
        ex.Which.ValidationErrors!.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task DeleteBatch_Idempotent_NoThrowOnSecondCall()
    {
        // The server contract is "DELETE returns 204 NoContent whether the def exists or not"
        // (idempotent). The client therefore must NOT throw on the second call.
        await using var client = RestUKBatchClientFactory.BuildRestOnly(_factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var created = await client.CreateBatchAsync(BuildCreateRequest(UniqueName("t05-del")), cts.Token);

        await client.DeleteBatchAsync(created.Id, cts.Token);

        // Second call must not throw.
        Func<Task> act = () => client.DeleteBatchAsync(created.Id, cts.Token);
        await act.Should().NotThrowAsync("DELETE /batches/by-id/{id} is idempotent — second call returns 204");

        // Verify the batch is genuinely gone.
        var fetched = await client.GetBatchByIdAsync(created.Id, cts.Token);
        fetched.Should().BeNull();
    }

    [Fact]
    public async Task DeleteBatch_CodeSource_Throws400ValidationFailed()
    {
        // Code-source batches are immutable: DELETE → 400 with type ukbatch:validation-failed
        // and `detail = "Code-source batches are immutable"`. (Code-source DOES carry ProblemType.)
        await using var client = RestUKBatchClientFactory.BuildRestOnly(_factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Sample.RestApi registers the InvoicePipeline batch with BatchSource.Code.
        var listed = await client.ListBatchesAsync(0, 50, nameContains: null, source: BatchSource.Code, cts.Token);
        listed.Items.Should().NotBeEmpty("Sample.RestApi registers Code-source batches at startup");
        var codeBatch = listed.Items[0];

        Func<Task> act = () => client.DeleteBatchAsync(codeBatch.Id, cts.Token);
        var ex = await act.Should().ThrowAsync<UKBatchClientException>();

        ex.Which.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ex.Which.ProblemType.Should().Be("ukbatch:validation-failed",
            "Code-source delete uses the explicit ukbatch:validation-failed URI (NOT the RFC URL); " +
            "but it carries NO `errors` dict, so the wizard's catch falls through to the generic toast.");
        ex.Which.ValidationErrors.Should().BeNull("Code-source 400 has NO field-errors dict (fall-through)");
    }
}
