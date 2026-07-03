#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────────────────────────
# UKBatch server + workers full-system END-TO-END ASSERT harness.
#
# The asserting, repeatable evolution of seed-batch.sh: instead of "create + trigger + read with your
# eyes", this drives the live Docker Compose stack (server + 3 workers + RabbitMQ + Postgres) over the
# REST API, POLLS each run to a terminal state, and FAILS HARD (exit 1) on any mismatch. The value over
# the in-process unit tests is the REAL image + REAL broker + REAL network + REAL Postgres path
# that static review and component tests cannot exercise.
#
# Scenarios:
#   S1  Stack health            — server + 3 workers /healthz 200; GET /api/workers → 3 Online.
#   S2  Simple sequential       — invoice@invoicing → ship@shipping → notify@notification; data forwarded
#                                  down the 3-service chain (ship reads invoiceId; notify reads invoiceId +
#                                  the trackingNumber ship produced).
#   S3  Approval + parallel      — parallel{invoice,notify} → approval gate → ship. Asserts the gate
#                                  REALLY holds (ship NOT dispatched while pending), then grants and
#                                  asserts all three cross-service executions Completed — and that ship
#                                  read the invoiceId forwarded out of the parallel group (cross-service
#                                  PARALLEL child path + parallel-fold output forwarding through the gate).
#   S4  Durability               — stop the shipping worker, trigger; invoice completes, ship's message
#                                  WAITS in the durable quorum queue; restart the worker within the RPC
#                                  timeout; ship completes (the broker's headline feature).
#   S6  Postgres state durability — restart ONLY the server; batch definitions + run history survive,
#                                  AND the in-flight run resumes: durable resume re-attaches the pending
#                                  gate, and granting it completes the run (the final ship reads the
#                                  invoiceId forwarded before the restart — output survives the resume).
#   (S5  onFailure/compensation  — needs a deliberately-failing job. See the S5 section below.)
#
# Prereq:  docker compose up -d --build --wait      (the full stack, from the repo root)
#          docker + bash + jq   (jq 1.6+; the stack also needs UKBATCH_DEV_AUTH=true for the S3 gate,
#                                which docker-compose.yml already sets).
# Usage:   bash samples/Sample.WorkerMode/e2e-assert.sh
#          BASE=http://localhost:5070/api bash samples/Sample.WorkerMode/e2e-assert.sh
# ─────────────────────────────────────────────────────────────────────────────────────────────────
set -uo pipefail

BASE="${BASE:-http://localhost:5070/api}"
ROOT="${BASE%/api}"                         # http://localhost:5070  (for /healthz, outside /api)
PASS=0
FAIL=0

pass() { printf '  \033[32m✓\033[0m %s\n' "$1"; PASS=$((PASS + 1)); }
fail() { printf '  \033[31m✗ %s\033[0m\n' "$1" >&2; FAIL=$((FAIL + 1)); }
section() { printf '\n\033[1m── %s ──\033[0m\n' "$1"; }

# ── HTTP helpers ──────────────────────────────────────────────────────────────────────────────────
http_code() { curl -sS -m 15 -o /dev/null -w '%{http_code}' "$@"; }   # status code only
get()       { curl -sS -m 15 "$@"; }                                  # body to stdout

# create_batch <name> <json-body> — idempotent AND self-healing: 201 creates; on 409 the stored copy
# (possibly seeded by an OLDER harness version and no longer accepted by current definition
# validation — e.g. an on-timeout action without a timeout) is deleted and recreated from the
# current body, so a long-lived demo stack can never pin a run to a stale, un-triggerable definition.
create_batch() {
  local name="$1" body="$2" code id
  code=$(curl -sS -m 15 -o /dev/null -w '%{http_code}' -X POST "$BASE/batches" \
    -H 'Content-Type: application/json' -d "$body")
  case "$code" in
    201) return 0 ;;
    409)
      id=$(get "$BASE/batches" | jq -r --arg n "$name" '.items[] | select(.name == $n) | .id' | head -1)
      if [ -z "$id" ]; then fail "create '$name' → 409 but no stored definition found by name"; return 1; fi
      curl -sS -m 15 -o /dev/null -X DELETE "$BASE/batches/by-id/$id"
      code=$(curl -sS -m 15 -o /dev/null -w '%{http_code}' -X POST "$BASE/batches" \
        -H 'Content-Type: application/json' -d "$body")
      [ "$code" = 201 ] && return 0
      fail "recreate '$name' after stale-definition delete → HTTP $code (expected 201)"; return 1 ;;
    *) fail "create '$name' → HTTP $code (expected 201/409)"; return 1 ;;
  esac
}

# trigger_batch <name> — POST a run by name, echo the batchId (run id) from the 202 body.
trigger_batch() {
  get -X POST "$BASE/batches/by-name/$1/run" -H 'Content-Type: application/json' -d '{}' \
    | jq -r '.batchId'
}

# status_json <batchRunId> — the run's executions page (PageEnvelope<JobExecution>).
status_json() { get "$BASE/batches/$1/status"; }

# wait_status <batchRunId> <timeout-secs> <jq-bool-filter> — poll the status page until the filter is
# true (exit 0) or the timeout elapses (exit 1). The filter runs against the PageEnvelope JSON.
wait_status() {
  local run="$1" timeout="$2" filter="$3" i
  for ((i = 0; i < timeout; i += 2)); do
    if status_json "$run" | jq -e "$filter" >/dev/null 2>&1; then return 0; fi
    sleep 2
  done
  return 1
}

# pending_approval_id <batchRunId> — echo the approvalId of the pending gate for this run (or empty).
pending_approval_id() {
  get "$BASE/approvals" | jq -r --arg b "$1" '.items[] | select(.batchId == $b) | .approvalId' | head -1
}

# ── batch definition bodies (enums are JSON strings — JsonStringEnumConverter both ends) ────────────
SIMPLE_BODY='{
  "name": "worker-mode-demo", "source": "Api", "failurePolicy": "StopOnFailure",
  "steps": [
    { "stepId": "step-1-invoice", "order": 0, "stepType": "Job",
      "job": { "jobName": "GenerateInvoice", "targetService": "invoicing" } },
    { "stepId": "step-2-ship", "order": 1, "stepType": "Job",
      "job": { "jobName": "ShipOrder", "targetService": "shipping" } },
    { "stepId": "step-3-notify", "order": 2, "stepType": "Job",
      "job": { "jobName": "SendNotification", "targetService": "notification" } }
  ]
}'

# parallel{invoice,notify} (order 0) → approval gate (order 1) → ship (order 2). Matches seed-batch.sh.
# ShipOrder (order 2) reads the invoiceId GenerateInvoice produced in the parallel group: the output
# folds out of the group into the run accumulator and forwards past the gate into ship's parameters.
# The gate sets NO timeout, so it waits indefinitely for the manual ops decision the scenario needs.
# onTimeout MUST still be sent (required member — omitting it is a binding 400) and MUST be "Fail":
# AutoApprove/Hold without a timeout duration are rejected by definition validation; Fail with no
# timeout simply never fires.
APPROVAL_PARALLEL_BODY='{
  "name": "approval-parallel-demo", "source": "Api", "failurePolicy": "StopOnFailure",
  "steps": [
    { "stepId": "step-1-parallel", "order": 0, "stepType": "ParallelGroup",
      "parallelGroup": { "joinPolicy": "WaitAll", "steps": [
        { "stepId": "step-1a-invoice", "order": 0, "stepType": "Job",
          "job": { "jobName": "GenerateInvoice", "targetService": "invoicing" } },
        { "stepId": "step-1b-notify", "order": 1, "stepType": "Job",
          "job": { "jobName": "SendNotification", "targetService": "notification" } }
      ] } },
    { "stepId": "step-2-approve", "order": 1, "stepType": "ApprovalGate",
      "approval": { "title": "Release the cross-service run", "allowedRoles": ["ops"], "onTimeout": "Fail" } },
    { "stepId": "step-3-ship", "order": 2, "stepType": "Job",
      "job": { "jobName": "ShipOrder", "targetService": "shipping" } }
  ]
}'

# Best-effort cleanup: if S4 aborts mid-way, leave the shipping worker running for the next operator.
cleanup() { docker compose start worker-shipping >/dev/null 2>&1 || true; }
trap cleanup EXIT

# ═══════════════════════════════════════════════════════════════════════════════════════════════════
# S1 — Stack health
# ═══════════════════════════════════════════════════════════════════════════════════════════════════
section "S1 — stack health (server + 3 workers + registry)"

[ "$(http_code "$ROOT/healthz")" = 200 ] && pass "server /healthz → 200" || fail "server /healthz not 200"

# Poll until all three workers register Online (heartbeat cadence ~15s; tolerant of a fresh server
# registry after a restart — the in-memory registry is rebuilt from the next round of heartbeats).
workers_json=""
for ((i = 0; i < 45; i += 3)); do
  workers_json=$(get "$BASE/workers")
  [ "$(printf '%s' "$workers_json" | jq '[.[] | select(.online)] | length')" = 3 ] && break
  sleep 3
done
online_count=$(printf '%s' "$workers_json" | jq '[.[] | select(.online)] | length')
[ "$online_count" = 3 ] && pass "3 workers Online" || fail "expected 3 Online workers, got $online_count"
for w in invoicing shipping notification; do
  printf '%s' "$workers_json" | jq -e --arg w "$w" '.[] | select(.name == $w and .online)' >/dev/null \
    && pass "worker '$w' Online" || fail "worker '$w' not Online"
done

# ═══════════════════════════════════════════════════════════════════════════════════════════════════
# S2 — Simple sequential cross-service (invoice@invoicing → ship@shipping)
# ═══════════════════════════════════════════════════════════════════════════════════════════════════
section "S2 — simple sequential cross-service (3-service chain, data forwarded down the chain)"

create_batch worker-mode-demo "$SIMPLE_BODY" || true
run=$(trigger_batch worker-mode-demo)
[ -n "$run" ] && pass "triggered (run=$run)" || fail "trigger returned no batchId"

if wait_status "$run" 60 '(.items | length) == 3 and ([.items[].status] | all(. == "Completed"))'; then
  pass "all 3 steps Completed"
else
  fail "run did not reach 3× Completed in 60s ($(status_json "$run" | jq -c '[.items[]|{j:.jobName,s:.status}]'))"
fi
status_json "$run" | jq -e '[.items[] | select(.jobName=="GenerateInvoice" and .workerName=="invoicing" and .status=="Completed")] | length == 1' >/dev/null \
  && pass "GenerateInvoice ran on 'invoicing'" || fail "GenerateInvoice not Completed on invoicing"
# ShipOrder read the forwarded invoiceId (from GenerateInvoice) and produced a trackingNumber of its own.
status_json "$run" | jq -e '[.items[] | select(.jobName=="ShipOrder" and .workerName=="shipping" and .status=="Completed" and (.parameters.invoiceId != null))] | length == 1' >/dev/null \
  && pass "ShipOrder ran on 'shipping' with the forwarded invoiceId" || fail "ShipOrder not Completed with forwarded invoiceId"
# SendNotification (the THIRD service) received values forwarded down the chain: invoiceId (2 hops, from
# GenerateInvoice) and trackingNumber (1 hop, from ShipOrder) — proof output flows across the whole pipeline.
status_json "$run" | jq -e '[.items[] | select(.jobName=="SendNotification" and .workerName=="notification" and .status=="Completed" and (.parameters.invoiceId != null) and (.parameters.trackingNumber != null))] | length == 1' >/dev/null \
  && pass "SendNotification on 'notification' got the forwarded invoiceId (2 hops) + trackingNumber (1 hop)" \
  || fail "SendNotification did not receive both forwarded values ($(status_json "$run" | jq -c '[.items[]|select(.jobName=="SendNotification")|{s:.status,inv:.parameters.invoiceId,trk:.parameters.trackingNumber}]'))"

# ═══════════════════════════════════════════════════════════════════════════════════════════════════
# S3 — Approval + parallel cross-service (the gate must REALLY hold)
# ═══════════════════════════════════════════════════════════════════════════════════════════════════
section "S3 — approval + parallel cross-service"

create_batch approval-parallel-demo "$APPROVAL_PARALLEL_BODY" || true
run=$(trigger_batch approval-parallel-demo)
[ -n "$run" ] && pass "triggered (run=$run)" || fail "trigger returned no batchId"

# Wait for the gate to become pending (the two parallel jobs run first, then the run pauses).
appid=""
for ((i = 0; i < 60; i += 2)); do
  appid=$(pending_approval_id "$run")
  [ -n "$appid" ] && break
  sleep 2
done
[ -n "$appid" ] && pass "approval gate pending (id=$appid)" || fail "no pending approval appeared in 60s"

# THE gate assertion: the two parallel jobs are Completed, but ship is NOT dispatched yet.
if status_json "$run" | jq -e '
      (.items | length) == 2
      and ([.items[].status] | all(. == "Completed"))
      and ([.items[] | select(.jobName == "ShipOrder")] | length == 0)' >/dev/null; then
  pass "gate holds — parallel (invoice+notify) done, ship NOT dispatched"
else
  fail "gate did not hold ($(status_json "$run" | jq -c '[.items[]|{j:.jobName,s:.status}]'))"
fi

# Grant it (DevAuth ops header — the server runs with UKBATCH_DEV_AUTH=true).
if [ -n "$appid" ]; then
  code=$(curl -sS -m 15 -o /dev/null -w '%{http_code}' -X POST "$BASE/approvals/$appid/approve" \
    -H 'Content-Type: application/json' -H 'X-Dev-User: e2e' -H 'X-Dev-Roles: ops' -d '{}')
  [ "$code" = 204 ] && pass "approve → 204" || fail "approve → HTTP $code (expected 204)"
fi

# After the grant, ship dispatches and all three cross-service executions complete.
if wait_status "$run" 60 '(.items | length) == 3 and ([.items[].status] | all(. == "Completed"))'; then
  pass "3 cross-service executions Completed after grant"
else
  fail "run did not reach 3× Completed after grant ($(status_json "$run" | jq -c '[.items[]|{j:.jobName,s:.status}]'))"
fi
status_json "$run" | jq -e '[.items[] | select(.jobName=="SendNotification" and .workerName=="notification" and .status=="Completed")] | length == 1' >/dev/null \
  && pass "SendNotification ran on 'notification' (parallel)" || fail "SendNotification not Completed on notification"
# ShipOrder ran AFTER the gate and read the invoiceId GenerateInvoice produced in the parallel group —
# the parallel-group output folds into the run accumulator and forwards past the approval gate.
status_json "$run" | jq -e '[.items[] | select(.jobName=="ShipOrder" and .workerName=="shipping" and .status=="Completed" and (.parameters.invoiceId != null))] | length == 1' >/dev/null \
  && pass "ShipOrder Completed on 'shipping' WITH the forwarded invoiceId (parallel-fold forwarding through the gate)" \
  || fail "ShipOrder not Completed with forwarded invoiceId ($(status_json "$run" | jq -c '[.items[]|{j:.jobName,s:.status,inv:.parameters.invoiceId}]'))"

# ═══════════════════════════════════════════════════════════════════════════════════════════════════
# S4 — Durability (stop the shipping worker; its message waits in the durable quorum queue)
# ═══════════════════════════════════════════════════════════════════════════════════════════════════
section "S4 — durability (worker stop → message waits → restart → completes)"

docker compose stop worker-shipping >/dev/null 2>&1 && pass "stopped worker-shipping" || fail "could not stop worker-shipping"
run=$(trigger_batch worker-mode-demo)
[ -n "$run" ] && pass "triggered with shipping down (run=$run)" || fail "trigger returned no batchId"

# Step 1 (invoice) completes on the still-running invoicing worker.
wait_status "$run" 30 '[.items[] | select(.jobName == "GenerateInvoice")] | (length > 0) and (.[0].status == "Completed")' \
  && pass "invoice Completed while shipping is down" || fail "invoice did not complete with shipping down"

# Step 2 (ship) must NOT be Completed — its message is parked in ukbatch.service.shipping (step 3 waits behind it).
status_json "$run" | jq -e '[.items[] | select(.jobName == "ShipOrder" and .status == "Completed")] | length == 0' >/dev/null \
  && pass "ship NOT completed (message waiting in quorum queue)" || fail "ship completed unexpectedly while worker down"

# Restart within the 30s RPC timeout window → the worker consumes the queued message and replies; the run
# then advances through ship and the final notify.
docker compose start worker-shipping >/dev/null 2>&1 && pass "restarted worker-shipping" || fail "could not restart worker-shipping"
if wait_status "$run" 45 '(.items | length) == 3 and ([.items[].status] | all(. == "Completed"))'; then
  pass "ship + notify Completed after worker restart (durable delivery)"
else
  fail "run did not complete after restart ($(status_json "$run" | jq -c '[.items[]|{j:.jobName,s:.status}]'))"
fi

# ═══════════════════════════════════════════════════════════════════════════════════════════════════
# S5 — onFailure / compensation (a cross-service step fails → OnFailureSteps run)
# ═══════════════════════════════════════════════════════════════════════════════════════════════════
section "S5 — onFailure / compensation (cross-service)"

# GenerateInvoice is invoked with { "fail": "true" } → the worker throws → FailurePolicy=Compensate
# routes to OnFailureSteps (a compensating SendNotification on the notification worker).
ONFAILURE_BODY='{
  "name": "onfailure-compensation-demo", "source": "Api", "failurePolicy": "Compensate",
  "steps": [
    { "stepId": "step-1-invoice-fail", "order": 0, "stepType": "Job",
      "job": { "jobName": "GenerateInvoice", "targetService": "invoicing", "parameters": { "fail": "true" } } }
  ],
  "onFailureSteps": [
    { "stepId": "comp-1-notify", "order": 0, "stepType": "Job",
      "job": { "jobName": "SendNotification", "targetService": "notification" } }
  ]
}'
create_batch onfailure-compensation-demo "$ONFAILURE_BODY" || true
run=$(trigger_batch onfailure-compensation-demo)
[ -n "$run" ] && pass "triggered (run=$run)" || fail "trigger returned no batchId"

if wait_status "$run" 60 '
      ([.items[] | select(.jobName == "GenerateInvoice" and .status == "Failed")] | length == 1)
      and ([.items[] | select(.jobName == "SendNotification" and .status == "Completed")] | length == 1)'; then
  pass "failing step → compensation ran (invoice Failed, notify compensated)"
else
  fail "onFailure/compensation did not resolve as expected ($(status_json "$run" | jq -c '[.items[]|{j:.jobName,s:.status}]'))"
fi
status_json "$run" | jq -e '[.items[] | select(.jobName=="GenerateInvoice" and .workerName=="invoicing" and .status=="Failed")] | length == 1' >/dev/null \
  && pass "GenerateInvoice Failed on 'invoicing' (injected)" || fail "invoice not Failed on invoicing"
status_json "$run" | jq -e '[.items[] | select(.jobName=="SendNotification" and .workerName=="notification" and .status=="Completed")] | length == 1' >/dev/null \
  && pass "compensation SendNotification Completed on 'notification'" || fail "compensation not Completed on notification"

# ═══════════════════════════════════════════════════════════════════════════════════════════════════
# S7 — partitioned fan-out (IPartitionedJob: SELECT-stream → 3 concurrent workers on the invoicing svc)
# ═══════════════════════════════════════════════════════════════════════════════════════════════════
section "S7 — partitioned fan-out (ReconcileInvoices, WithParallelism(3))"

# NOTE: the server-side cross-service SHADOW row carries status only — the worker's Processed/Failed
# counters do NOT flow back over JobResult (known limitation). The parallelism proof lives in the
# WORKER's logs (overlapping START waves of 3) + the wall-clock (12×700ms ≈ ~3s on 3 workers, not ~8.4s).
PARTITIONED_BODY='{
  "name": "partitioned-demo", "source": "Api", "failurePolicy": "StopOnFailure",
  "steps": [
    { "stepId": "step-1-reconcile", "order": 0, "stepType": "Job",
      "job": { "jobName": "ReconcileInvoices", "targetService": "invoicing" } }
  ]
}'
create_batch partitioned-demo "$PARTITIONED_BODY" || true
# Anchor the worker-log greps to an absolute pre-trigger timestamp. A fixed `--since 3m`-style
# window is a flake on a long-lived stack: slow polls can push the grep past the window, and the
# window can also swallow a PREVIOUS run's lines (false-green). An anchor captured here is exact.
t_run=$(date -u +%Y-%m-%dT%H:%M:%SZ)
run=$(trigger_batch partitioned-demo)
[ -n "$run" ] && pass "triggered (run=$run)" || fail "trigger returned no batchId"

if wait_status "$run" 60 '[.items[] | select(.jobName == "ReconcileInvoices" and .status == "Completed")] | length == 1'; then
  pass "partitioned job Completed on 'invoicing' (12 rows / 3 workers)"
else
  fail "partitioned job did not complete ($(status_json "$run" | jq -c '[.items[]|{j:.jobName,s:.status}]'))"
fi
# The 12 DONE lines in the worker log are the per-item proof (best-effort — docker may be absent).
# NOTE: grep -c prints "0" AND exits 1 on zero matches — an `|| echo 0` fallback would append a
# SECOND line and break the integer comparison below; the ${var:-0} default is the safe guard.
done_count=$(docker logs ukbatch-worker-invoicing --since "$t_run" 2>/dev/null | grep -c 'ReconcileInvoices: DONE')
[ "${done_count:-0}" -ge 12 ] && pass "worker log shows ${done_count} DONE items (all rows processed)" \
  || fail "expected >=12 DONE lines in worker log, got ${done_count:-0}"

# Per-run worker-count override: a run-level {"ukbatch.workers": N} parameter beats the
# registration-time WithParallelism. Proof = the runner's override log line on the worker.
t_override=$(date -u +%Y-%m-%dT%H:%M:%SZ)
run=$(get -X POST "$BASE/batches/by-name/partitioned-demo/run" -H 'Content-Type: application/json' \
  -d '{"initialParameters":{"ukbatch.workers":6}}' | jq -r '.batchId')
if wait_status "$run" 60 '[.items[] | select(.jobName == "ReconcileInvoices" and .status == "Completed")] | length == 1'; then
  pass "override run Completed (ukbatch.workers=6)"
else
  fail "override run did not complete"
fi
# NOTE: capture-then-grep (no `docker logs | grep -q` pipe): with `set -o pipefail`, grep -q's early
# exit SIGPIPEs docker logs (exit 141) and the WHOLE pipeline reads as failed — a false-red.
recent_log=$(docker logs ukbatch-worker-invoicing --since "$t_override" 2>/dev/null || true)
grep -q "worker count 6 (per-run 'ukbatch.workers' override" <<<"$recent_log" \
  && pass "worker log confirms the per-run worker-count override (6, registered default 3)" \
  || fail "per-run override log line not found on the worker"
# FinalizeAsync (unit-of-work commit hook): exactly-once bulk commit AFTER all 12 rows.
grep -q "FINALIZE — bulk commit (simulated): AddRange(12)" <<<"$recent_log" \
  && pass "FinalizeAsync ran once with all 12 accumulated results (unit-of-work commit)" \
  || fail "FinalizeAsync bulk-commit log line not found on the worker"

# ═══════════════════════════════════════════════════════════════════════════════════════════════════
# S6 — Postgres server-state durability (restart ONLY the server)
# ═══════════════════════════════════════════════════════════════════════════════════════════════════
section "S6 — Postgres server-state durability (server restart)"

# Set up state that must survive the restart: definitions, a finished run's history, and an in-flight
# run parked at its approval gate (so we can assert durable resume re-attaches it after the restart).
defs_before=$(get "$BASE/batches" | jq '.totalCount')
hist_run=$(trigger_batch approval-parallel-demo)
appid_before=""
for ((i = 0; i < 40; i += 2)); do
  appid_before=$(pending_approval_id "$hist_run")
  [ -n "$appid_before" ] && break
  sleep 2
done
[ -n "$appid_before" ] && pass "in-flight run parked at approval gate before restart" \
  || fail "gate never became pending before restart (cannot exercise S6)"

docker compose restart ukbatch-server >/dev/null 2>&1 && pass "restarted ukbatch-server" || fail "could not restart server"
# Wait for the server to come back healthy (EF migrate-on-start + DI boot).
for ((i = 0; i < 90; i += 3)); do
  [ "$(http_code "$ROOT/healthz")" = 200 ] && break
  sleep 3
done
[ "$(http_code "$ROOT/healthz")" = 200 ] && pass "server healthy again after restart" || fail "server did not become healthy after restart"

# (a) Definitions persisted in Postgres (durable server state — the demo's two, not fewer than before).
defs_after=$(get "$BASE/batches" | jq '.totalCount')
{ [ "${defs_after:-0}" -ge 2 ] && [ "${defs_after:-0}" -ge "${defs_before:-0}" ]; } \
  && pass "batch definitions persisted ($defs_after)" || fail "definitions not persisted (before=$defs_before after=$defs_after)"

# (b) Completed run history persisted — the parallel jobs that finished BEFORE the gate survive.
status_json "$hist_run" | jq -e '[.items[] | select(.status == "Completed")] | length >= 1' >/dev/null \
  && pass "completed run history persisted (finished executions survive restart)" || fail "run history lost after restart"

# (c) DURABLE RESUME — the paused run RESUMES. A graceful restart leaves the in-flight run in Postgres
#     and DurableRunRecovery re-attaches the pending gate, so it is STILL pending + re-decidable after the
#     restart. Granting it lets the run continue to the final ship step, which reads the invoiceId the
#     parallel group produced BEFORE the restart.
# Recovery runs asynchronously after boot (healthz turns 200 before the run is re-launched and its gate
# re-attached), so poll — same window as the pre-restart gate wait above.
appid_after=""
for ((i = 0; i < 40; i += 2)); do
  appid_after=$(pending_approval_id "$hist_run")
  [ -n "$appid_after" ] && break
  sleep 2
done
[ -n "$appid_after" ] && pass "in-flight gate survives restart — run resumes (durable resume re-attaches it)" \
  || fail "gate not pending after restart (durable resume should re-attach the gate)"
# Grant the re-attached gate: the resumed run completes its final ship step, reading the invoiceId the
# parallel group produced before the restart — the forwarded cross-service output survives the resume.
if [ -n "$appid_after" ]; then
  code=$(curl -sS -m 15 -o /dev/null -w '%{http_code}' -X POST "$BASE/approvals/$appid_after/approve" \
    -H 'Content-Type: application/json' -H 'X-Dev-User: e2e' -H 'X-Dev-Roles: ops' -d '{}')
  [ "$code" = 204 ] && pass "granted the re-attached gate after restart → 204" || fail "grant after restart → HTTP $code (expected 204)"
  if wait_status "$hist_run" 60 '[.items[] | select(.jobName == "ShipOrder" and .status == "Completed" and (.parameters.invoiceId != null))] | length == 1'; then
    pass "resumed run completed — final ship read the invoiceId forwarded before the restart (output survives resume)"
  else
    fail "resumed run did not complete with the forwarded invoiceId ($(status_json "$hist_run" | jq -c '[.items[]|{j:.jobName,s:.status,inv:.parameters.invoiceId}]'))"
  fi
fi

# ═══════════════════════════════════════════════════════════════════════════════════════════════════
# Summary
# ═══════════════════════════════════════════════════════════════════════════════════════════════════
printf '\n\033[1m═══ e2e: %d passed, %d failed ═══\033[0m\n' "$PASS" "$FAIL"
[ "$FAIL" -eq 0 ] || exit 1
