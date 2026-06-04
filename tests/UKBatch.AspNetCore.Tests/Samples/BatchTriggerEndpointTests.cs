using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using UKBatch.AspNetCore.Tests.Helpers;
using Xunit;

namespace UKBatch.AspNetCore.Tests.Samples;

/// <summary>
/// test #17a — RUNTIME regression for the lookup migration in
/// <c>Sample.BatchWorkflow.Program</c>. The 25-line reflection block at
/// <c>Program.cs:60-84</c> was replaced with a 3-line <see cref="UKBatch.Abstractions.Batches.IBatchDefinitionLookup"/>
/// call; this test bootstraps the sample host via <see cref="WebApplicationFactory{TEntryPoint}"/>,
/// POSTs to <c>/batches/run</c>, and asserts the response body contains a non-null/non-empty
/// <c>batchId</c>. No source-grep here — that lives in
/// <c>tests/UKBatch.Core.Tests/Samples/SampleSourceGuardTests.cs</c>.
/// </summary>
public sealed class BatchTriggerEndpointTests
{
    [Fact]
    public async Task Sample_BatchWorkflow_PostBatchesRun_Returns200WithBatchId()
    {
        // Reuse the env-var bridge from BatchWorkflowSmokeTests — Sample.BatchWorkflow reads
        // Sample:ApprovalTimeoutSeconds at builder.Configuration BEFORE the WebApplicationFactory
        // ConfigureWebHost callback runs.
        const string EnvKey = "Sample__ApprovalTimeoutSeconds";
        var priorValue = Environment.GetEnvironmentVariable(EnvKey);
        try
        {
            Environment.SetEnvironmentVariable(EnvKey, "30");
            using var factory = new WebApplicationFactory<Sample.BatchWorkflow.Program>();
            using var client = factory.CreateClient();
            client.WithDevAuth("alice", "ops");

            var response = await client.PostAsync(new Uri("/batches/run", UriKind.Relative), content: null);

            response.StatusCode.Should().Be(HttpStatusCode.OK,
                "the lookup-based startup must produce a working /batches/run endpoint");

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            doc.RootElement.TryGetProperty("batchId", out var batchIdProp).Should().BeTrue();
            var batchId = batchIdProp.GetString();
            batchId.Should().NotBeNullOrWhiteSpace(
                "POST /batches/run must return a non-empty batchId via IBatchDefinitionLookup");
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvKey, priorValue);
        }
    }
}
