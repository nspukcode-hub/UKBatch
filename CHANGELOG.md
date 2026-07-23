# Changelog

All notable changes to UKBatch are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.2.4-alpha] - 2026-07-19

Sign-in for the dashboard and the API through an external identity provider: users log in with OpenID Connect, and both the screen and the API act on the roles the provider issues.

### Added

- **OpenID Connect login and role-gating** — a new package, `UKBatch.AspNetCore.OpenIdConnect`. `AddUKBatchOpenIdConnect(...)` wires a cookie session with an interactive OpenID Connect login for the dashboard and JWT bearer validation for the API, over any standard provider (Keycloak, Azure AD, Auth0, IdentityServer) reached by its authority URL — there is no provider-specific code. It defines two roles: a **viewer** (any signed-in user) reads, and an **operator** (a role name you map to your provider's) performs write actions such as trigger, create, cancel, and retry. Approval gates keep their own per-gate allowed-roles check, so an approver who holds a gate's role can still decide it without being an operator.
- **Role-gating the API** — `RequireUKBatchRoleAuthorization()` on the `MapUKBatchApi` group gates read endpoints to the viewer policy and write endpoints to the operator policy, while worker heartbeat ingest stays open. Endpoints are classified in the library; calling the method is opt-in, so an ungated host is unchanged.
- **Per-user token forwarding** — when the dashboard runs under OpenID Connect, each signed-in user's access token flows from the dashboard to the API, so the API sees the real user and their roles, and an approval is recorded under the real person rather than a shared service identity. A server-side token store keyed by the user carries the token across the Blazor Server render boundary and refreshes it when it nears expiry.
- **Nested-role flattening** — providers that nest roles inside a claim (for example Keycloak's `realm_access.roles` and `resource_access.*.roles`) are flattened to standard role claims automatically, so the policies and the approval-gate check read them without extra provider configuration. The claim paths are configurable.
- **Dashboard sign-in awareness** — the dashboard cascades the signed-in user's identity, hides or disables write controls a viewer cannot use, shows who is signed in with a sign-out control, and adds a session-diagnostics panel that lists the roles and claim types the server sees. With no authentication wired, every control renders as before.
- **Server OpenID Connect posture** — `UKBatch.Server` accepts an OpenID Connect posture via `UKBATCH_OIDC_AUTHORITY` (plus client id/secret, audience, and operator roles), alongside the existing anonymous and dev-auth postures. `MapUKBatchSignOut` maps a sign-out endpoint. A runnable `docker-compose.oidc.yml` starts Keycloak with a pre-imported realm.

### Changed

- An approval's recorded identity now falls back from the name claim to `preferred_username` then `sub`, so a signed-in user without a mapped name claim is still recorded by their real identity rather than as anonymous.

### Known limitations

- The role model is two levels (viewer/operator). A fine-grained permission matrix, an audit log, and user/role management screens are not part of this release.
- Interactive browser cookie-login works end to end. The one requirement is that the identity provider and the dashboard appear to the browser under one shared site — the reverse-proxy / shared-hostname (and HTTPS) setup a real deployment already has — because the login callback and its correlation cookie are same-site. For local development over plain HTTP, placing both under one parent domain (same-site) is enough; no HTTPS is required, and the correlation/nonce cookies are issued without the `Secure` flag only when HTTPS metadata is not required.
- Worker-to-server heartbeat is still anonymous (observability only, off the dispatch path).
- With `SaveTokens` the session cookie holds the tokens under Data Protection; a multi-replica deployment needs a shared Data Protection key ring, and a large token may require cookie chunking.

## [0.2.3-alpha] - 2026-07-15

Data flow and correctness between the steps of a batch: a step can hand values to the steps after it, a failure can undo the work already done, and a flow can skip a step or route to one of several branches based on what it finds.

### Added

- **Step output forwarding.** A job can publish values through `JobContext.Outputs`, and every later step in the run reads them as ordinary parameters — so a pipeline can pass an id, a total, or an object from one step to the next instead of recomputing it. Values merge in a fixed order (a run's initial parameters, then accumulated outputs, then a step's own static parameters), so a static parameter always wins over a forwarded one. A parallel group folds back the outputs its join policy accepted (all branches for `WaitAll`, the winner for `WaitAny`, the quorum for `WaitMajority`). Forwarding also crosses a service boundary: a job on a worker returns its outputs to the orchestrator over HTTP or RabbitMQ. The forwarded state is persisted (`BatchRun.ForwardedState`), so a resumed run continues with the values it had. `JobParameters.TryGet<T>`/`GetRequired<T>`/`GetOrDefault<T>` now also resolve values that crossed a JSON boundary, which is what makes a forwarded object readable as its own type. An additive migration (`AddStepOutputForwarding`) adds the output columns.
- **Saga / per-step compensation.** A step can declare a compensator (`BatchStep.Compensation`, in code via `CompensateWith...`), and when a later step fails the run undoes the completed steps **in reverse order** before it stops. The step that failed is not compensated (it did not complete), and a cancelled run never compensates. The unwind is durable: a cursor (`BatchRun.CompensationStepIndex`) records how far it got, so a host restart mid-unwind continues rather than starting over, and a compensator effectively runs once. A compensator may target another service. An additive migration (`AddBatchRunCompensationCursor`) adds the cursor.
- **Retrying a failed run.** `POST /batches/{runId}/retry` (and a Retry button on the dashboard's run detail) starts a fresh run that picks up from the failed run's cursor instead of re-running the steps that already succeeded. The new run links back through `RetryOfBatchId`; the original stays Failed as a record. A compensated run is not retryable.
- **Declared job parameters.** A job can declare what it expects — `.WithParameter<T>(name, defaultValue, required, description)` — and that declaration surfaces in three places: the dashboard's Trigger screen renders typed inputs instead of a raw JSON box, `GET /jobs/{name}` publishes the parameters in its OpenAPI-described response, and a worker announces its jobs' parameters in its heartbeat so a remote job is described too. Triggering a single job without a required parameter is rejected with a clear `400` (`ukbatch:job-parameter-validation`); the check can be turned off with `UKBatchOptions.EnforceDeclaredParameters`. Jobs that declare nothing behave exactly as before.
- **Conditional step execution (run-if).** A step can carry a condition (`.RunIf(...)`, `condition` in the REST body, or the dashboard editors) and is skipped when it does not hold at dispatch time. A skipped step is recorded — the new terminal `JobStatus.Skipped` — rather than silently vanishing, the run continues to the next step, and a skipped step is never compensated during an unwind. An if/else is two consecutive steps with opposite conditions.
- **Decision step.** A decision routes to exactly one of several job branches: branches are evaluated in order, the first whose condition holds runs, and a branch with no condition is the default (at most one, last). The branches not taken are recorded `Skipped` and the flow re-converges on the next step. Declared in code with `.Decide(...)`/`.ThenDecide(...)`, and rendered on the dashboard — in the run view, the wizard preview, and the visual editor, which fans the decision out as you build it and gives each branch a colour shared by its condition chip, its arrow and its card.

### Fixed

- **A resumed run no longer loses the parameters it was triggered with.** A run's initial parameters are persisted with it, so a run that resumes after a restart sees the same values it started with instead of an empty set.

### Known limitations

- Step outputs are JSON-serializable values (scalars and objects). Passing a large payload by file/artifact reference is a later release.
- A compensator attaches to a top-level step: a parallel group or a decision compensates as one unit. Per-child and per-branch compensators are a later release.
- Each decision branch runs one job — chain decisions for more depth. A parallel group cannot be a decision branch.
- A declared parameter is an announcement, not a contract: enforcement applies to the single-job trigger endpoint. A job run as a batch step or by a schedule still reads its parameters at run time.
- Skipped steps are not reflected in a run's counts (total/succeeded/failed), so a run containing skipped steps shows fewer accounted steps than it has.
- Durable compensation (the unwind cursor) and forwarded state need the EF storage adapter, like durable resume before them; with in-memory storage they are inactive.

## [0.2.2-alpha] - 2026-06-18

Durable recovery for batch workflows: an in-flight run resumes after a restart, and a scheduled batch can catch up a fire it missed while down. Both build on the persistent run store and require the EF storage adapter.

### Added

- **Durable batch resume.** A batch run interrupted by a host restart resumes from where it left off instead of being lost. The run records a step cursor (`BatchRun.CurrentStepIndex`); at startup the EF storage adapter re-launches each in-flight run under a resume policy — `ResumeForward` (skip already-completed steps, the default), `RestartAll`, or `RestartFrom(n)`. A completed step is not re-run, and a pending approval gate is re-attached so a later decision still resolves it. An additive migration (`AddBatchRunCursor`) adds the cursor column; existing tables are unchanged.
- **Per-batch missed-fire scheduler catch-up.** A scheduled batch can opt in to replaying a fire it missed while the host was down, via `BatchDefinition.ScheduleCatchUpWindow` (set in code with `.CatchUpMissedWithin(...)`, in the REST body as `scheduleCatchUpWindow`, or on the dashboard wizard's Schedule step). On restart the scheduler replays only the most recent occurrence missed within the window — exactly once (coalesced, never a burst) — and a persisted last-fire watermark guarantees the same occurrence never fires twice. Leaving the window unset keeps the previous skip-on-downtime behaviour. An additive migration (`AddScheduleCatchUp`) adds the `ScheduleStates` watermark table and the per-batch window column.

### Changed

- **A graceful host shutdown no longer cancels an in-flight batch run.** The run is left in-flight and resumed on the next start (see durable resume above); an explicit administrative cancel still ends the run as Cancelled.

### Known limitations

- Durable resume and scheduler catch-up require the EF storage adapter (PostgreSQL or SQLite). With the default in-memory storage they are inactive — there is no durable store to record the cursor or the last-fire watermark.
- Single-node: resume and catch-up act on the node that boots and do not coordinate across multiple instances sharing one database. Distributed/HA recovery is a later release.
- Scheduler catch-up replays at most one occurrence per batch per restart (the latest missed within the window); a gap older than the window is left for the operator to run manually.

## [0.2.1-alpha] - 2026-06-15

Run-store foundation and the visible features it unlocks: persistent run history, batch scheduling, and cancelling a stuck run.

### Added

- **Persistent batch run history.** Each batch run is recorded as a first-class `BatchRun` (one per trigger), so run history survives restarts with the EF storage adapter. A new additive migration (`AddBatchRuns`) creates the `BatchRuns` table; existing tables are unchanged. The dashboard Executions page gains an **"Executions | Runs" view toggle** — the "Runs" tab paginates one row per run (authoritative status, step count, and per-status job counts), replacing the previous client-side grouping of execution rows.
- **Batch cron scheduling.** `BatchDefinition.Schedule` now actually runs the batch on its cron expression. Previously a batch schedule was stored but never executed (only job schedules fired). A scheduled batch created or edited from the dashboard or API begins firing immediately, without a restart.
- **Cancel a running batch.** `POST /batches/{batchRunId}/cancel` and a dashboard "Cancel run" button end an in-flight run — including one parked at an approval gate that nobody can decide. Cancellation is an administrative action whose authorization is independent of the gate's allowed roles, so it can release a gate no current operator holds the role for; the run ends as Cancelled.

### Fixed

- **A batch's "Recent runs" history list now shows a gate-failed run as Failed.** The run store records the authoritative terminal status from the runtime, so a run that ended at a rejected or timed-out-Fail approval gate is shown as Failed in the run history and the Runs view — not Completed. This resolves the run-history limitation noted in 0.2.0-alpha (the run detail page was already correct).

### Known limitations

- The batch scheduler is in-memory: schedules that come due while the host is down are skipped, not replayed — the same behaviour as the existing job scheduler. A durable scheduler is a later release.
- A run interrupted by a host crash stays in the in-progress ("Running") state in run history; its executions are reaped to Failed, but the run record itself is not reaped until a later release.
- Cancelling a run tears down its orchestration and releases a parked approval gate; a local job already dispatched to a worker runs to its own completion unless it observes its own cancellation token. Full per-job cancellation propagation is a later release.

## [0.2.0-alpha] - 2026-06-13

First slice of the v0.2 line — stability and developer-experience groundwork ahead of durable resume. All changes are in the free, MIT-licensed packages.

### Added

- **`JobContext.WorkerIndex`** — partitioned jobs and inline `ParallelForEachAsync` bodies can read the 0-based worker slot (`[0, workerCount)`) to shard per-worker side state (for example, a connection from a sized pool). A plain `IJob` with no fan-out always reads `0`.
- **`[Job]` attribute parity with the fluent builder** — `PartitionWorkerCount` and `ItemErrorPolicy` are now settable on the attribute. Previously they were fluent-only, so attribute-discovered partitioned jobs were forced to the default worker count and `FailFast`.

### Changed

- **Triggering a batch that references an unregistered local job is now rejected at trigger time** with HTTP 400 (`ukbatch:batch-trigger-validation`), listing the offending step(s), instead of being accepted (202) and silently producing zero executions. Genuine runtime job failures (a registered job that throws) are unaffected and still route through the failure/compensation policy.
- **`ApprovalGateConfig.OnTimeout` is now optional and defaults to `Fail`.** Omitting it from a request body no longer produces a bare 400.
- **Request-body binding failures now return RFC 7807 `application/problem+json`** (empty or malformed body) instead of a bare 400.
- **The Workers page caps each worker's advertised jobs** at eight chips with a "+N more" expander, so a worker exposing many jobs no longer stretches its table row.
- **Job routing tags are validated** — `JobBuilder.WithTags(...)` and `[Job(Tags = ...)]` now reject null, empty, or whitespace tag values instead of storing them as-is.

### Fixed

- **A run that ends at an approval gate — rejected or timed out to Fail — is now shown as Failed** (both the run status and the gate node), instead of appearing Completed. Approval gates have no execution row, so a gate-induced failure was previously invisible to the run's rolled-up status, which was computed only from job rows. The dashboard now colours each gate node from its own recorded outcome. (Known limitation: a batch's "Recent runs" history list still derives each row's status from job rows, so it can still show a gate-failed run as Completed; the run detail page is correct, and the list is resolved by the durable run store in a later release.)
- **Several dashboard components rendered unstyled** (status badges, progress bars, the service-health dot, worker chips, the batch-step list, running-row highlighting) because their rules lived in scoped stylesheets the host never linked. The rules are now in the global stylesheet, so these elements display as intended.
- **The DAG node inspector's close button stays visible** when a node carries a long status badge (e.g. `AwaitingApproval`) — the badge no longer pushes the close button out of the panel.
- **A batch step whose `TargetService` is empty or whitespace now runs locally** (in-process), consistent with the wizard's normalization of blank target services — previously a stray blank value could be dispatched as a (failing) cross-service call.

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

[Unreleased]: https://github.com/nspukcode-hub/UKBatch/compare/v0.2.3-alpha...HEAD
[0.2.3-alpha]: https://github.com/nspukcode-hub/UKBatch/releases/tag/v0.2.3-alpha
[0.2.2-alpha]: https://github.com/nspukcode-hub/UKBatch/releases/tag/v0.2.2-alpha
[0.2.1-alpha]: https://github.com/nspukcode-hub/UKBatch/releases/tag/v0.2.1-alpha
[0.2.0-alpha]: https://github.com/nspukcode-hub/UKBatch/releases/tag/v0.2.0-alpha
[0.1.6-alpha]: https://github.com/nspukcode-hub/UKBatch/releases/tag/v0.1.6-alpha
[0.1.5-alpha]: https://github.com/nspukcode-hub/UKBatch/releases/tag/v0.1.5-alpha
[0.1.4-alpha]: https://github.com/nspukcode-hub/UKBatch/releases/tag/v0.1.4-alpha
[0.1.3-alpha]: https://github.com/nspukcode-hub/UKBatch/releases/tag/v0.1.3-alpha
[0.1.1-alpha]: https://github.com/nspukcode-hub/UKBatch/releases/tag/v0.1.1-alpha
[0.1.0-alpha]: https://github.com/nspukcode-hub/UKBatch/releases/tag/v0.1.0-alpha
