---
title: Changelog
description: Release notes and the known limitations of the UKBatch preview.
---

All notable changes to UKBatch are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.3-alpha] - 2026-06-08

### Fixed

- **The dashboard no longer flashes a spurious "Disconnected" banner on first load.** The
  service conductor was connecting to the (embedded) SignalR hub before the host had finished
  starting, so the initial connect failed and the banner stayed red until a manual reconnect or
  the 60-second retry. The initial connect is now deferred until the host has started, and the
  retry interval is shortened, so the dashboard connects cleanly on first load.

## [0.1.1-alpha] - 2026-06-08

### Added

- **Typed partitioned-job batch steps** — `RunPartitionedJob<TJob>()` and
  `ThenRunPartitionedJob<TJob>()` on the batch, parallel-group, and on-failure builders, so a
  partitioned job can be added to a batch by type like a regular job. Backed by a new
  `IPartitionedJobMarker` base interface.
- **`AddUKBatchDevAuth()`** — an opt-in, header-based development authentication scheme in
  `UKBatch.AspNetCore`, so an embedded host can exercise the approval buttons in a demo without
  hand-writing an authentication handler. It trusts `X-Dev-User` / `X-Dev-Roles` with no
  verification and refuses to start in the Production environment unless explicitly allowed.

### Changed

- **OpenAPI `servers` URLs no longer carry a trailing slash**, so a Postman / OpenAPI client
  importing the document no longer builds double-slashed request paths that 404.
- **A dashboard service `BaseUrl` is normalized to a trailing slash automatically** —
  `http://host/api` now behaves the same as `http://host/api/`, removing a long-standing
  configuration footgun.
- **Approval gates reject an inconsistent timeout configuration** — choosing an `AutoApprove`
  or `Hold` on-timeout action now requires a timeout duration, validated on both the dashboard
  and the server; the run-detail panel shows "no timeout — waits indefinitely" when a gate has
  none.
- **The dashboard sidebar and breadcrumb update immediately when switching services** — the
  layout now reacts to the current-service change instead of lagging one navigation behind.
- **CI workflows** updated to the current GitHub Actions majors (Node 24 runtime).

### Fixed

- **A job completing immediately after startup could be missed by the awaiter**, leaving a
  caller waiting until its timeout. The process-wide watch subscription is now registered
  synchronously before `StartAsync` returns.
- **`UKBatch.Dashboard` now raises build warning `UKBATCH001` when a .NET 10 host has not set
  `<RequiresAspNetWebAssets>true</>`**, instead of failing silently with a runtime 404 for
  `_framework/blazor.web.js`. The property must be set by the host project — NuGet cannot supply
  it during restore — and the docs now say so plainly. (.NET 8 hosts are unaffected.)

## [0.1.0-alpha] - 2026-06-06

First public preview of the UKBatch package family.

### Added

- **Multi-targeting** — every package ships `net8.0` and `net10.0` builds in a single NuGet
  package; the consuming app's target framework picks the right build automatically. On
  `net8.0` the EF Core adapter rides the EF Core 8 (LTS) line.
- **UKBatch.Abstractions** — zero-dependency contracts (interfaces, attributes, DTOs) shared by
  every package.
- **UKBatch.Core** — the runtime: dispatcher, cron scheduler, per-job retries,
  sequential/parallel/approval-gate workflows, partitioned data-parallel jobs, the in-memory
  store, and the in-process transport.
- **UKBatch.AspNetCore** — host integration with `HttpContext`-aware `TriggeredBy` enrichment,
  W3C trace propagation, and a readiness health check.
- **UKBatch.Api** — REST endpoints, an OpenAPI document, and a SignalR hub for live job-status
  updates.
- **UKBatch.Dashboard** — a Blazor Server UI for monitoring, triggering, approvals, a visual
  drag-and-drop batch editor, a live DAG view, and multi-service support.
- **UKBatch.Worker** — `UseWorkerMode` to turn a microservice into a worker, with a worker
  identity, a heartbeat, and a startup transport guard.
- **UKBatch.Transport.Http** — HMAC-SHA256-signed cross-service messaging with retry and
  circuit-breaker resilience.
- **UKBatch.Transport.RabbitMQ** — durable quorum queues, request-reply RPC, and
  effectively-once dedupe.
- **UKBatch.Storage.EntityFrameworkCore** — PostgreSQL and SQLite persistence with design-time
  migrations.
- **UKBatch.Server** — a standalone, configuration-driven Docker application, plus a
  `docker-compose` setup for a server + workers deployment.

## Known limitations

This is a 0.1.x-alpha preview. The current limitations:

- **No OpenAPI document on .NET 8.** Built-in OpenAPI generation requires .NET 9+; on the
  `net8.0` target the REST + SignalR surface is identical, but `/openapi/v1.json` is not
  produced (layer Swashbuckle yourself if needed).
- **No durable workflow resume.** After a host restart, batch definitions and completed history
  persist (with persistent storage), but in-flight executions are marked `Failed` by the orphan
  reaper and paused approval gates do not resume.
- **No step output forwarding.** A step's output is not passed as input to subsequent steps. The
  cross-service HTTP sample's `orderId` illustrates this: it is generated but not forwarded.
- **No cross-service progress forwarding.** Per-item progress counters of a job running on a
  remote worker appear in the worker's logs but do not flow back to the dashboard.
- **Rejected approval gate with successful compensation.** When a gate is rejected and its
  compensation steps succeed, the overall run reports as `Completed`; the rejection is visible
  in the approvals history, not in the final run status.
- **In-memory transport dedupe.** Transport message-dedupe caches are in-memory per process and
  reset on restart.
- **Single-node orphan reaper.** The orphaned-execution reaper assumes a single orchestrator
  node.
- **Adapters not yet available.** Kafka, Azure Service Bus, and Redis adapters are not part of
  this release.

[0.1.3-alpha]: https://github.com/nspukcode-hub/UKBatch/releases/tag/v0.1.3-alpha
[0.1.1-alpha]: https://github.com/nspukcode-hub/UKBatch/releases/tag/v0.1.1-alpha
[0.1.0-alpha]: https://github.com/nspukcode-hub/UKBatch/releases/tag/v0.1.0-alpha
