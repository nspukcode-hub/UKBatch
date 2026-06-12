# Changelog

All notable changes to UKBatch are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.6-alpha] - 2026-06-12

### Fixed

- **Pagination metadata is honest: `totalCount` is now the filter-wide total.** `POST /executions/query` and `GET /batches/{batchRunId}/status` returned the page size as `totalCount`, so paginating clients (including the dashboard's own Executions page) computed "no more pages" and could never advance past page one.
- **Abbreviated ids in the dashboard are distinguishable again.** Lists shortened ids to their FIRST 8 characters — for UUIDv7 ids that is the millisecond-timestamp region, so runs created within the same ~65-second window looked identical. Abbreviations now show the random tail (`…6bf9ccba`) instead.
- **The batch-run page's execution order no longer depends on how you arrived.** The initial fetch was newest-first while live updates appended at the bottom; the table is now consistently newest-first.

### Changed

- **Long execution tables are bounded.** The job detail page's "Recent executions" and the batch-run page's "Executions" are now capped live windows showing the 50 most recent rows (newest at the top; new arrivals push the oldest out) with a "View all in Executions" link. The batch detail page's run history is paginated at 30 rows per page.
- **The Executions page accepts deep links** — `?jobName=` and `?batchId=` query parameters pre-fill the corresponding filters, so the "View all" links land on an already-filtered, fully paginated list.
- **Full ids are copyable.** Abbreviated ids are display-only, so the batch-run page and the execution detail page now surface the full id with a copy-to-clipboard button (the Executions filters are exact-match and need the whole id). The batch-run page also links to its filtered Executions view permanently, not only past the 50-row cap.
- **Batch schedules are labelled honestly.** A cron expression stored on a batch definition is not executed by the runtime yet (batch cron scheduling is planned); the wizard and the batch detail page now say so instead of silently accepting a schedule that never fires. Job-level cron schedules (`[Job(Schedule = ...)]` / builder registration) are unaffected and run as before.

## [0.1.5-alpha] - 2026-06-12

### Fixed

- **Scheduled jobs no longer fire twice.** Two distinct duplicate-fire bugs were found and fixed:
  - *Clock skew:* the scheduler's timer could complete marginally before the wall-clock deadline (timer rounding, NTP slew), firing an occurrence early and then re-arming that same occurrence. The loop now re-checks the deadline after every wake and anchors the next occurrence no earlier than the one just fired.
  - *Duplicate registration:* a job registered explicitly through the builder under a custom name was registered a second time by attribute discovery under its attribute-derived name, arming its `[Job(Schedule = ...)]` cron twice. Discovery now skips any implementation type that is already registered — the explicit registration wins.
- **A scheduler fire that fails to enqueue no longer strands a `Pending` execution row** — the created row is compensated to `Failed` with a descriptive error.
- **Lifecycle hardening for the scheduler and the runtime host:** shutdown waits are bounded by `ShutdownTimeout` and honor the host's grace token; `StartAsync` is one-shot (a duplicate start is a logged no-op instead of doubling workers and leaking the stopping source); the linked stopping sources are disposed, safely even when the service provider is torn down before `StopAsync` runs; an abort during startup now reaches the worker loops.
- **A flaky transport test raced CI load** — the short-timeout request-reply test budget was widened; no production change.

### Changed

- **Cron documentation corrected.** The documented schedule examples were five-field crontab expressions, which the six-field seconds-first default format rejects at startup. Examples are now six-field, the format is stated explicitly (with the `CronFormat.Standard` opt-in for five-field expressions), and the `JobAttribute.Schedule` API documentation describes the actual contract.
- **The API samples run with a plain `dotnet run`** — launch profiles pin the port and the `Development` environment (the development-only auth scheme refuses `Production` by design), and the readmes document the `-f` flag the multi-targeted samples require.
- **The package readmes and the root README link the documentation website** (<https://nspukcode-hub.github.io/UKBatch/>).

## [0.1.4-alpha] - 2026-06-10

### Added

- **Official server Docker image** — `ghcr.io/nspukcode-hub/ukbatch-server` (multi-platform: `linux/amd64` + `linux/arm64`), published automatically alongside the NuGet packages on every release. The demo Compose stack still builds its images from source (now tagged `:local`).
- **Documentation website** — guides and concepts at <https://nspukcode-hub.github.io/UKBatch>.

### Security

- **Approval role claims are read only from an authenticated principal** — an unauthenticated request can no longer present role claims to the approval endpoints.
- **HTTP transport request-body buffering is capped** — `MaxBodyBytes` is validated into the 1 byte – 16 MB range, bounding pre-authentication memory use.
- **HTTP transport dedupe cache no longer grows unbounded** — the message-id dedupe cache is a self-contained LRU whose result map is evicted in lock-step with the id set.
- **RabbitMQ refuses insecure defaults against a remote broker** — connecting to a non-loopback broker with the default `guest`/`guest` credentials now fails at host start unless the new `AllowInsecureBroker=true` option is set explicitly (loopback brokers are exempt). If your deployment relied on a remote demo broker with default credentials, set this option or — better — create a dedicated broker user.

### Fixed

- **Abrupt host shutdown no longer races disposal in two background pumps** — the SignalR status fan-out and the RabbitMQ consumer pump now guard their cancellation source against concurrent stop/dispose, eliminating spurious `ObjectDisposedException`s.
- **A cancelled HTTP transport subscription is treated as a graceful stop** during shutdown instead of being logged as an error.

### Changed

- Internal cleanups only beyond the above: dead code and test-only helpers removed; no public API changes.

## [0.1.3-alpha] - 2026-06-08

### Fixed

- **The dashboard no longer flashes a spurious "Disconnected" banner on first load.** The service conductor was connecting to the (embedded) SignalR hub before the host had finished starting, so the initial connect failed and the banner stayed red until a manual reconnect or the 60-second retry. The initial connect is now deferred until the host has started, and the retry interval is shortened, so the dashboard connects cleanly on first load.

## [0.1.1-alpha] - 2026-06-08

### Added

- **Typed partitioned-job batch steps** — `RunPartitionedJob<TJob>()` and `ThenRunPartitionedJob<TJob>()` on the batch, parallel-group, and on-failure builders, so a partitioned job can be added to a batch by type like a regular job. Backed by a new `IPartitionedJobMarker` base interface.
- **`AddUKBatchDevAuth()`** — an opt-in, header-based development authentication scheme in `UKBatch.AspNetCore`, so an embedded host can exercise the approval buttons in a demo without hand-writing an authentication handler. It trusts `X-Dev-User` / `X-Dev-Roles` with no verification and refuses to start in the Production environment unless explicitly allowed.

### Changed

- **OpenAPI `servers` URLs no longer carry a trailing slash**, so a Postman / OpenAPI client importing the document no longer builds double-slashed request paths that 404.
- **A dashboard service `BaseUrl` is normalized to a trailing slash automatically** — `http://host/api` now behaves the same as `http://host/api/`, removing a long-standing configuration footgun.
- **Approval gates reject an inconsistent timeout configuration** — choosing an `AutoApprove` or `Hold` on-timeout action now requires a timeout duration, validated on both the dashboard and the server; the run-detail panel shows "no timeout — waits indefinitely" when a gate has none.
- **The dashboard sidebar and breadcrumb update immediately when switching services** — the layout now reacts to the current-service change instead of lagging one navigation behind.
- **CI workflows** updated to the current GitHub Actions majors (Node 24 runtime).

### Fixed

- **A job completing immediately after startup could be missed by the awaiter**, leaving a caller waiting until its timeout. The process-wide watch subscription is now registered synchronously before `StartAsync` returns.
- **`UKBatch.Dashboard` now raises build warning `UKBATCH001` when a .NET 10 host has not set `<RequiresAspNetWebAssets>true</>`**, instead of failing silently with a runtime 404 for `_framework/blazor.web.js`. The property must be set by the host project — NuGet cannot supply it during restore — and the docs now say so plainly. (.NET 8 hosts are unaffected.)

## [0.1.0-alpha] - 2026-06-06

First public preview of the UKBatch package family.

### Added

- **Multi-targeting** — every package ships `net8.0` and `net10.0` builds in a single NuGet package; the consuming app's target framework picks the right build automatically. On `net8.0` the EF Core adapter rides the EF Core 8 (LTS) line.
- **UKBatch.Abstractions** — zero-dependency contracts (interfaces, attributes, DTOs) shared by every package.
- **UKBatch.Core** — the runtime: dispatcher, cron scheduler, per-job retries, sequential/parallel/approval-gate workflows, partitioned data-parallel jobs, the in-memory store, and the in-process transport.
- **UKBatch.AspNetCore** — host integration with `HttpContext`-aware `TriggeredBy` enrichment, W3C trace propagation, and a readiness health check.
- **UKBatch.Api** — REST endpoints, an OpenAPI document, and a SignalR hub for live job-status updates.
- **UKBatch.Dashboard** — a Blazor Server UI for monitoring, triggering, approvals, a visual drag-and-drop batch editor, a live DAG view, and multi-service support.
- **UKBatch.Worker** — `UseWorkerMode` to turn a microservice into a worker, with a worker identity, a heartbeat, and a startup transport guard.
- **UKBatch.Transport.Http** — HMAC-SHA256-signed cross-service messaging with retry and circuit-breaker resilience.
- **UKBatch.Transport.RabbitMQ** — durable quorum queues, request-reply RPC, and effectively-once dedupe.
- **UKBatch.Storage.EntityFrameworkCore** — PostgreSQL and SQLite persistence with design-time migrations.
- **UKBatch.Server** — a standalone, configuration-driven Docker application, plus a `docker-compose` setup for a server + workers deployment.

### Known limitations

- **No OpenAPI document on .NET 8.** Built-in OpenAPI generation requires .NET 9+; on the `net8.0` target the REST + SignalR surface is identical, but `/openapi/v1.json` is not produced (layer Swashbuckle yourself if needed).
- **No durable workflow resume.** After a host restart, batch definitions and completed history persist (with persistent storage), but in-flight executions are marked `Failed` by the orphan reaper and paused approval gates do not resume.
- **No step output forwarding.** A step's output is not passed as input to subsequent steps. The cross-service HTTP sample's `orderId` illustrates this: it is generated but not forwarded.
- **No cross-service progress forwarding.** Per-item progress counters of a job running on a remote worker appear in the worker's logs but do not flow back to the dashboard.
- **Rejected approval gate with successful compensation.** When a gate is rejected and its compensation steps succeed, the overall run reports as `Completed`; the rejection is visible in the approvals history, not in the final run status.
- **In-memory transport dedupe.** Transport message-dedupe caches are in-memory per process and reset on restart.
- **Single-node orphan reaper.** The orphaned-execution reaper assumes a single orchestrator node.
- **Adapters not yet available.** Kafka, Azure Service Bus, and Redis adapters are not part of this release.

[Unreleased]: https://github.com/nspukcode-hub/UKBatch/compare/v0.1.6-alpha...HEAD
[0.1.6-alpha]: https://github.com/nspukcode-hub/UKBatch/releases/tag/v0.1.6-alpha
[0.1.5-alpha]: https://github.com/nspukcode-hub/UKBatch/releases/tag/v0.1.5-alpha
[0.1.4-alpha]: https://github.com/nspukcode-hub/UKBatch/releases/tag/v0.1.4-alpha
[0.1.3-alpha]: https://github.com/nspukcode-hub/UKBatch/releases/tag/v0.1.3-alpha
[0.1.1-alpha]: https://github.com/nspukcode-hub/UKBatch/releases/tag/v0.1.1-alpha
[0.1.0-alpha]: https://github.com/nspukcode-hub/UKBatch/releases/tag/v0.1.0-alpha
