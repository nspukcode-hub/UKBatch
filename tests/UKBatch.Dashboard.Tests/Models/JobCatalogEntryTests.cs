using FluentAssertions;
using UKBatch.Abstractions.Workers;
using UKBatch.Dashboard.Configuration;
using UKBatch.Dashboard.Models;
using Xunit;

namespace UKBatch.Dashboard.Tests.Models;

/// <summary>
/// Unit tests for <see cref="JobCatalogEntry"/>: the worker-aware "job @ service" catalog
/// that feeds the wizard/editor Job-step dropdown. Pure logic (no bunit) — the union/dedupe/sort and the
/// Target-service merge are reliably testable without a render, and these are the load-bearing invariants
/// the catalog correctness rests on.
/// </summary>
public sealed class JobCatalogEntryTests
{
    private static WorkerInfo Worker(string name, params string[] jobs) => new()
    {
        Name = name,
        Jobs = jobs,
        LastSeenUtc = DateTimeOffset.UtcNow,
        Online = true,
    };

    private static UKBatchServiceDescriptor Descriptor(string name) => new()
    {
        Name = name,
        BaseUrl = new Uri($"http://{name}.local:5000/api/"),
        DisplayName = name,
    };

    // ── Build: the union of worker-advertised + local jobs ────────────────────────

    [Fact]
    public void Build_UnionsWorkerAndLocalJobs_WithServiceAttribution()
    {
        var workers = new[]
        {
            Worker("invoicing", "GenerateInvoice"),
            Worker("shipping", "ShipOrder"),
            Worker("notification", "SendNotification"),
        };
        var local = new[] { "LocalJob" };

        var catalog = JobCatalogEntry.Build(workers, local);

        catalog.Should().Contain(new JobCatalogEntry("GenerateInvoice", "invoicing"));
        catalog.Should().Contain(new JobCatalogEntry("ShipOrder", "shipping"));
        catalog.Should().Contain(new JobCatalogEntry("SendNotification", "notification"));
        catalog.Should().Contain(new JobCatalogEntry("LocalJob", null),
            "local jobs are folded in with ServiceName=null (embedded mode keeps working)");
        catalog.Should().HaveCount(4);
    }

    [Fact]
    public void Build_DedupesOnJobNameAndService_Ordinal()
    {
        // The same (job, service) advertised twice (e.g. two beats / a worker also listed locally) collapses.
        var workers = new[]
        {
            Worker("invoicing", "GenerateInvoice", "GenerateInvoice"), // duplicate within one worker
            Worker("invoicing", "GenerateInvoice"),                    // duplicate across workers (same name)
        };
        var local = new[] { "GenerateInvoice" }; // same NAME but local (null service) ⇒ a DISTINCT entry

        var catalog = JobCatalogEntry.Build(workers, local);

        catalog.Where(e => e.JobName == "GenerateInvoice" && e.ServiceName == "invoicing")
            .Should().HaveCount(1, "(GenerateInvoice, invoicing) dedupes to one entry");
        catalog.Where(e => e.JobName == "GenerateInvoice" && e.ServiceName == null)
            .Should().HaveCount(1, "(GenerateInvoice, local) is a DISTINCT pair — same name, different target");
        catalog.Should().HaveCount(2);
    }

    [Fact]
    public void Build_SortsStably_ByJobNameThenServiceLocalFirst()
    {
        var workers = new[] { Worker("zeta", "BravoJob"), Worker("alpha", "BravoJob") };
        var local = new[] { "BravoJob", "AlphaJob" };

        var catalog = JobCatalogEntry.Build(workers, local);

        // AlphaJob (local) first; then BravoJob group: local (null) before "alpha" before "zeta".
        catalog.Select(e => (e.JobName, e.ServiceName)).Should().ContainInOrder(
            ("AlphaJob", (string?)null),
            ("BravoJob", null),
            ("BravoJob", "alpha"),
            ("BravoJob", "zeta"));
    }

    [Fact]
    public void Build_ToleratesEmptyInputs_AndSkipsBlankNames()
    {
        JobCatalogEntry.Build([], []).Should().BeEmpty("empty catalog must render (best-effort)");

        var workers = new[] { Worker("w", "Real", "", "   ") };
        var local = new[] { "", "AlsoReal" };
        var catalog = JobCatalogEntry.Build(workers, local);

        catalog.Should().BeEquivalentTo(new[]
        {
            new JobCatalogEntry("AlsoReal", null),
            new JobCatalogEntry("Real", "w"),
        }, "blank/whitespace job names are skipped");
    }

    [Fact]
    public void Build_OrchestratorWithNoLocalJobs_StillOffersWorkerJobs()
    {
        // The exact problem: a pure orchestrator (server + workers mode) returns from /api/jobs, but the
        // workers advertise theirs — the catalog must NOT be empty.
        var catalog = JobCatalogEntry.Build(new[] { Worker("invoicing", "GenerateInvoice") }, []);

        catalog.Should().ContainSingle()
            .Which.Should().Be(new JobCatalogEntry("GenerateInvoice", "invoicing"));
    }

    // ── MergeTargetServices: worker names become routing-key targets ───────────────

    [Fact]
    public void MergeTargetServices_AddsWorkerNamesNotAlreadyConfigured()
    {
        var configured = new[] { Descriptor("billing") };
        var workers = new[] { Worker("invoicing", "GenerateInvoice"), Worker("shipping", "ShipOrder") };

        var merged = JobCatalogEntry.MergeTargetServices(configured, workers);

        // Configured descriptors first (registration order), then the distinct worker names.
        merged.Select(s => s.Name).Should().ContainInOrder("billing", "invoicing", "shipping");
        merged.Should().HaveCount(3);
    }

    [Fact]
    public void MergeTargetServices_DoesNotDuplicateConfiguredName()
    {
        // A worker whose name matches a configured descriptor must NOT be added twice.
        var configured = new[] { Descriptor("invoicing") };
        var workers = new[] { Worker("invoicing", "GenerateInvoice") };

        var merged = JobCatalogEntry.MergeTargetServices(configured, workers);

        merged.Should().ContainSingle("the configured 'invoicing' is not duplicated by the worker of the same name");
        merged.Single().Name.Should().Be("invoicing");
    }

    [Fact]
    public void MergeTargetServices_NoWorkers_ReturnsConfiguredUnchanged()
    {
        var configured = new[] { Descriptor("a"), Descriptor("b") };

        var merged = JobCatalogEntry.MergeTargetServices(configured, []);

        merged.Select(s => s.Name).Should().Equal("a", "b");
    }
}
