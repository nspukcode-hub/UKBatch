# Changelog

All notable changes to UKBatch are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0-alpha] - 2026-06-05

First public preview of the UKBatch package family.

### Added

- **UKBatch.Abstractions** — zero-dependency contracts (interfaces, attributes, DTOs) shared by every package.
- **UKBatch.Core** — the runtime: dispatcher, cron scheduler (via Cronos), per-job retries, sequential/parallel/approval-gate workflows, partitioned data-parallel jobs, the in-memory store, and the in-process transport.
- **UKBatch.AspNetCore** — host integration with `HttpContext`-aware `TriggeredBy` enrichment, W3C trace propagation, and a readiness health check.
- **UKBatch.Api** — REST endpoints, an OpenAPI document, and a SignalR hub for live job-status updates.
- **UKBatch.Dashboard** — a Blazor Server UI for monitoring, triggering, approvals, a visual drag-and-drop batch editor, a live DAG view, and multi-service support.
- **UKBatch.Worker** — `UseWorkerMode` to turn a microservice into a worker, with a worker identity, a heartbeat, and a startup transport guard.
- **UKBatch.Transport.Http** — HMAC-SHA256-signed cross-service messaging with retry and circuit-breaker resilience.
- **UKBatch.Transport.RabbitMQ** — durable quorum queues, request-reply RPC, and effectively-once dedupe.
- **UKBatch.Storage.EntityFrameworkCore** — PostgreSQL and SQLite persistence with design-time migrations.
- **UKBatch.Server** — a standalone, configuration-driven Docker application, plus a `docker-compose` setup for a server + workers deployment.

### Known limitations

- **No durable workflow resume.** After a host restart, batch definitions and completed history persist (with persistent storage), but in-flight executions are marked `Failed` by the orphan reaper and paused approval gates do not resume.
- **No step output forwarding.** A step's output is not passed as input to subsequent steps. The cross-service HTTP sample's `orderId` illustrates this: it is generated but not forwarded.
- **No cross-service progress forwarding.** Per-item progress counters of a job running on a remote worker appear in the worker's logs but do not flow back to the dashboard.
- **Rejected approval gate with successful compensation.** When a gate is rejected and its compensation steps succeed, the overall run reports as `Completed`; the rejection is visible in the approvals history, not in the final run status.
- **In-memory transport dedupe.** Transport message-dedupe caches are in-memory per process and reset on restart.
- **Single-node orphan reaper.** The orphaned-execution reaper assumes a single orchestrator node.
- **Adapters not yet available.** Kafka, Azure Service Bus, and Redis adapters are not part of this release.
