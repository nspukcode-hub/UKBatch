---
title: Workflow building blocks
description: Approval gates, partitioned data-parallel jobs, and attribute-based job discovery.
---

Batches compose real patterns through the fluent builder: sequential
(`RunJob<A>().ThenRunJob<B>()`), parallel fan-out/fan-in (`ThenInParallel(...)`), approval
gates, and compensation (`OnFailure(...)`). This page covers the three building blocks most
people reach for first.

## Approval gates

Pause a batch until a human approves or rejects from the dashboard:

```csharp
b.AddBatch("rollout", batch => batch
    .RunJob<DeployJob>()
    .ThenWaitForApproval(
        title: "Confirm rollout",
        roles: new[] { "ops" },
        timeout: TimeSpan.FromMinutes(30),
        onTimeout: ApprovalTimeoutAction.Hold));
```

The gate holds until an authenticated caller with a matching role approves it. Roles are
matched against `ClaimTypes.Role` by default — see [Gotchas](/UKBatch/concepts/gotchas/) if
you use Azure AD / Auth0 / SAML.

:::caution[Approving needs an authenticated caller]
The approve/reject endpoints derive the approver from `HttpContext.User`, so an app with no
authentication rejects every decision — anonymous callers cannot approve, and even a wildcard
`"*"` role gate still requires an *authenticated* user. For local development and demos, add
the header-based dev scheme instead of hand-writing an auth handler:

```csharp
builder.Services.AddUKBatchDevAuth();   // dev/demo only — refuses to start in Production
builder.Services.AddAuthorization();
// ... then in the pipeline:
app.UseAuthentication();
app.UseAuthorization();
```

Callers then send `X-Dev-User` and `X-Dev-Roles: ops` headers. `AddUKBatchDevAuth` trusts
those headers with **no verification** and throws on startup in the Production environment
unless you opt in explicitly — for production, wire real authentication (e.g. `AddJwtBearer`
+ OIDC) and map your identity provider's role claim.
:::

A few more rules worth knowing:

- **Set a timeout when the on-timeout action acts.** If `onTimeout` is `AutoApprove` or
  `Hold`, you must also give a `timeout` — otherwise the gate waits indefinitely and the
  action never fires. The wizard and the REST validators reject that combination.
  `onTimeout: Fail` with no timeout is valid: the gate just waits until a human decides.
- **A pending gate is a snapshot of its definition at creation time.** Editing a batch
  definition does not change a gate that is already waiting — its roles, timeout, and
  on-timeout action are fixed when the run reaches the gate. A stuck, undecidable gate is
  resolved by deciding it through the approvals API with proper authentication, or by
  restarting the host.

## Partitioned (data-parallel) jobs

For "fetch a set of items, then process them on N workers", implement
`IPartitionedJob<TItem>`. The runtime owns the producer/consumer plumbing (a bounded channel
plus N consumer tasks); you declare the source stream and the per-item work, with an optional
commit hook:

```csharp
public sealed class ReconcileInvoicesJob : IPartitionedJob<int>
{
    // Stream the items to process. Yield lazily so the bounded channel applies backpressure.
    public async IAsyncEnumerable<int> SourceAsync(JobContext context, CancellationToken ct)
    {
        context.Progress.SetTotal(100);          // drives a live x/100 progress counter
        for (var id = 1; id <= 100; id++)
            yield return id;
    }

    // Runs on N concurrent workers — MUST be thread-safe.
    public Task ProcessAsync(int id, JobContext context, CancellationToken ct) =>
        ReconcileAsync(id, ct);

    // Optional commit hook: runs exactly once after every item, single-threaded.
    // Skipped on a fail-fast abort or cancellation; under ContinueOnError it commits the subset that succeeded.
    public Task FinalizeAsync(JobContext context, CancellationToken ct) =>
        SaveResultsAsync(ct);
}
```

Register it with a worker count and a per-item error policy:

```csharp
b.AddPartitionedJob<ReconcileInvoicesJob, int>()
    .Named("ReconcileInvoices")
    .WithParallelism(4)
    .WithItemErrorPolicy(ItemErrorPolicy.ContinueOnError);
```

The worker count can be overridden per run by passing the trigger parameter
`ukbatch.workers` (an invalid value falls back to the configured parallelism with a warning,
and the effective count is capped at 128).

## Attribute discovery

Instead of registering each job explicitly, decorate it with `[Job]` and scan assemblies:

```csharp
[Job(Name = "DailyReport", Schedule = "0 0 9 * * *", MaxRetries = 3, TimeoutSeconds = 600)]
public sealed class DailyReportJob : IJob { /* ... */ }

builder.AddUKBatchAspNetCore(b => b.ScanAssemblies(typeof(Program).Assembly));
```

`[Job]` carries optional `Name`, `Schedule` (cron), `MaxRetries`, `TimeoutSeconds`, and `Tags`.

:::note[Cron format]
Schedules are **six fields with seconds first** by default
(`sec min hour day month day-of-week` — Cronos `CronFormat.IncludeSeconds`). "Daily at 09:00" is
`0 0 9 * * *`; "every 30 seconds" is `*/30 * * * * *`. A classic five-field crontab expression
such as `0 9 * * *` is **rejected at startup**; to use five-field expressions set
`UKBatchOptions.CronFormat = CronFormat.Standard`.
:::
