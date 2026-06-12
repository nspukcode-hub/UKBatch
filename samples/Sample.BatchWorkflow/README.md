# Sample.BatchWorkflow

ASP.NET Core host demonstrating the full UKBatch batch workflow: sequential + parallel + approval gate + compensation.

The pipeline (`invoice-pipeline`):

1. `InvoiceGenerationJob` (sequential)
2. Parallel: `EmailNotificationJob` + `SmsNotificationJob` (join policy: WaitAll)
3. Approval gate ("Confirm rollout", role `ops`, configurable timeout, auto-approve on timeout)
4. `ArchiveJob`
5. On any failure: `RollbackJob` (failure policy: Compensate)

## REQUIRED — `ctx.RestoreRequestActivity()` opt-in

Every `IJob.ExecuteAsync` in this sample opens with `using var _ = ctx.RestoreRequestActivity();` — REQUIRED for trace correlation across the trigger boundary. See the `UKBatch.AspNetCore` README for details.

## Configurable approval timeout

`appsettings.json` -> `Sample:ApprovalTimeoutSeconds` (default 30). The integration tests override this to 2 for deterministic fast runs.

## Run

```bash
dotnet run --project samples/Sample.BatchWorkflow -f net10.0
```

`-f` is required because the sample multi-targets `net10.0;net8.0`. The launch profile pins
`http://localhost:5002` and the `Development` environment (the development-only auth scheme
refuses to start in `Production` by design).

## Endpoints

```bash
# Trigger the pipeline (X-Dev-User and X-Dev-Roles required by the approve endpoints)
curl -X POST -H "X-Dev-User: alice" -H "X-Dev-Roles: ops" http://localhost:5002/batches/run
# {"batchId":"<id>"}

# List child executions
curl http://localhost:5002/batches/<batchId>/status

# List pending approvals (optionally filtered by role)
curl "http://localhost:5002/approvals?role=ops"

# Approve / reject (requires X-Dev-Roles: ops)
curl -X POST -H "X-Dev-User: alice" -H "X-Dev-Roles: ops" \
  http://localhost:5002/approvals/<approvalId>/approve
curl -X POST -H "X-Dev-User: alice" -H "X-Dev-Roles: ops" \
  "http://localhost:5002/approvals/<approvalId>/reject?reason=ChangedMind"

# Readiness
curl http://localhost:5002/healthz
```

DevAuth is for local development ONLY. Replace with cookies, JWT, or any other ASP.NET Core auth scheme in production.
