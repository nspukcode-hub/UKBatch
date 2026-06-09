using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using UKBatch.AspNetCore.Tests.Helpers;
using Xunit;

namespace UKBatch.AspNetCore.Tests.Samples;

/// <summary>
/// Smoke tests for <c>Sample.BatchWorkflow</c>. S4 — the approval-gate timeout is overridden to
/// 2s via <see cref="IConfiguration"/> so the auto-approve path runs deterministically.
/// </summary>
public sealed class BatchWorkflowSmokeTests
{
    private static WebApplicationFactory<Sample.BatchWorkflow.Program> CreateFactory(int approvalTimeoutSeconds)
    {
        // Sample.BatchWorkflow.Program reads `Sample:ApprovalTimeoutSeconds` from
        // `builder.Configuration` BEFORE the WebApplicationFactory's ConfigureWebHost callback
        // runs, so AddInMemoryCollection is too late. The env var route is observed during the
        // initial Configuration build pass — see Microsoft.Extensions.Configuration default sources.
        return new WebApplicationFactory<Sample.BatchWorkflow.Program>().WithWebHostBuilder(b =>
        {
            // Set the env var for the whole process for the test's lifetime.
            Environment.SetEnvironmentVariable(
                "Sample__ApprovalTimeoutSeconds",
                approvalTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            b.ConfigureAppConfiguration(c =>
            {
                c.AddInMemoryCollection(new[]
                {
                    new KeyValuePair<string, string?>(
                        "Sample:ApprovalTimeoutSeconds",
                        approvalTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                });
            });
        });
    }

    [Fact]
    public async Task Batch_WithApprovalTimeoutSeconds2_AutoApprovesAndArchiveCompletes()
    {
        // fix: capture the env-var's prior value and restore it in finally so the test
        // does not leak `Sample__ApprovalTimeoutSeconds=2` into the rest of the process.
        const string EnvKey = "Sample__ApprovalTimeoutSeconds";
        var priorValue = Environment.GetEnvironmentVariable(EnvKey);
        try
        {
            using var factory = CreateFactory(2);
            using var client = factory.CreateClient();
            client.WithDevAuth("alice", "ops");

            var triggerResponse = await client.PostAsync(new Uri("/batches/run", UriKind.Relative), content: null);
            var triggerBody = await triggerResponse.ShouldBeAsync(HttpStatusCode.OK);
            using var triggerDoc = JsonDocument.Parse(triggerBody);
            var batchId = triggerDoc.RootElement.GetProperty("batchId").GetString();
            batchId.Should().NotBeNullOrEmpty();

            // Poll to a generous deadlock backstop (auto-approve fires at 2s; a healthy run finishes in
            // ~2.x s, so 30s is generous-but-bounded). The 250ms delay is at the END of the loop so the
            // first status check is immediate — a tight 8s ceiling flaked on a loaded 2-core runner where
            // host boot + scheduler latency + in-process dispatch ate most of the window.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            bool archiveCompleted = false;
            string lastSeen = "<none>";
            while (DateTime.UtcNow < deadline)
            {
                var statusResponse = await client.GetAsync(new Uri($"/batches/{batchId}/status", UriKind.Relative));
                var statusBody = await statusResponse.ShouldBeAsync(HttpStatusCode.OK);
                lastSeen = statusBody;
                using var statusDoc = JsonDocument.Parse(statusBody);
                var executions = statusDoc.RootElement.GetProperty("executions");
                foreach (var exec in executions.EnumerateArray())
                {
                    var jobName = exec.GetProperty("jobName").GetString();
                    var status = exec.GetProperty("status").GetInt32();
                    if (jobName is not null &&
                        jobName.EndsWith("ArchiveJob", StringComparison.Ordinal) &&
                        status == (int)UKBatch.Abstractions.Models.JobStatus.Completed)
                    {
                        archiveCompleted = true;
                        break;
                    }
                }
                if (archiveCompleted) break;
                await Task.Delay(250);
            }

            archiveCompleted.Should().BeTrue(
                $"ArchiveJob must reach Completed status within the polling window; " +
                $"auto-approve gate fires at 2s and then ArchiveJob runs in-process. " +
                $"Last seen status body: {lastSeen}");
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvKey, priorValue);
        }
    }
}
