#!/usr/bin/env bash
# Seed + trigger the cross-service batches for the Sample.WorkerMode server + workers demo.
#
# Two demos, run back-to-back:
#
#   1) worker-mode-demo (simple) — three sequential cross-service Job steps, data forwarded down the chain:
#        step 1: GenerateInvoice  on the "invoicing"    worker (produces invoiceId + invoice),
#        step 2: ShipOrder        on the "shipping"     worker (reads them, produces trackingNumber),
#        step 3: SendNotification on the "notification" worker (reads the forwarded invoiceId + trackingNumber).
#
#   2) approval-parallel-demo — parallel fan-out -> approval gate -> ship (forwarded), ALL three
#      workers, all cross-service:
#        step 1: ParallelGroup (joinPolicy:"WaitAll") — GenerateInvoice@invoicing + SendNotification@notification
#                run concurrently (watch the two DAG nodes go blue -> green together); GenerateInvoice's
#                invoiceId output folds out of the group into the run,
#        step 2: ApprovalGate (allowedRoles:["ops"], no timeout + onTimeout:"Fail" — waits
#                indefinitely; AutoApprove/Hold without a timeout duration are rejected by
#                definition validation, and onTimeout is a required member so it must be sent) —
#                pauses the run AFTER the two parallel jobs complete, until an "ops" caller grants it,
#        step 3: ShipOrder on the "shipping" worker — reads the invoiceId forwarded from step 1's
#                parallel GenerateInvoice (cross-service parallel-fold forwarding through the gate).
#      The approval gate needs an authenticated "ops" caller. The server must run with
#      UKBATCH_DEV_AUTH=true (docker-compose sets it) so it can be granted via curl with the role header
#      (X-Dev-User + X-Dev-Roles: ops). The browser dashboard approve button cannot inject that header,
#      so curl is the approval path (full OIDC/login is a v0.2 concern).
#
# Each step's message is routed over RabbitMQ to the matching worker's durable quorum service queue
# (ukbatch.service.{name}); the worker consumes it, runs the job, and replies via direct-reply-to.
#
# Prereq: `docker compose up --build` is running (server reachable at http://localhost:5070; the three
#         workers Online in the dashboard Workers panel).
# Usage:  ./seed-batch.sh                         # run BOTH demos (simple, then approval+parallel)
#         BASE=http://localhost:5070/api ./seed-batch.sh
#
# NOTE on routing names (verified against Sample.CrossServiceRabbitMQ):
#   * JobName   = the [Job(Name = "...")] attribute value on the worker job
#                 (GenerateInvoiceJob -> "GenerateInvoice", ShipOrderJob -> "ShipOrder",
#                  SendNotificationJob -> "SendNotification").
#   * targetService = the worker's WorkerName / UseWorkerMode -> "invoicing" / "shipping" /
#                 "notification". This is the ROUTING KEY and MUST match the worker's WorkerName
#                 (Ordinal). A mismatch is silent: the message waits forever in the quorum queue.
set -euo pipefail

BASE="${BASE:-http://localhost:5070/api}"

# ─────────────────────────────────────────────────────────────────────────────────────────────────
# create_batch <name> <json-body>
#   POSTs a batch definition. Treats 201 (created) and 409 (already exists) as success; aborts on any
#   other status. Enums serialize/deserialize as STRINGS (JsonStringEnumConverter on both ends):
#   source: "Api" | stepType: "Job" | "ParallelGroup" | "ApprovalGate" | failurePolicy: "StopOnFailure"
#   | joinPolicy: "WaitAll" | onTimeout: "Fail".
# ─────────────────────────────────────────────────────────────────────────────────────────────────
create_batch() {
  local name="$1" body="$2" http id=""
  echo "==> Creating batch definition '${name}' at ${BASE}/batches"
  http=$(curl -sS -o /tmp/ukbatch-seed-create.json -w '%{http_code}' \
    -X POST "${BASE}/batches" \
    -H 'Content-Type: application/json' \
    -d "${body}")
  echo "    HTTP ${http}"
  cat /tmp/ukbatch-seed-create.json; echo
  if [ "${http}" = "409" ]; then
    # A definition with this name already exists — possibly an OLD demo shape from an earlier seed
    # whose steps no longer match the current worker jobs (the jobs read forwarded parameters, so a
    # stale topology would fail at run time). Replace it so the trigger below runs the documented shape.
    echo "    (definition already exists — replacing it with the current demo shape)"
    curl -sS "${BASE}/batches/by-name/${name}" -o /tmp/ukbatch-seed-existing.json
    if command -v jq >/dev/null 2>&1; then
      id=$(jq -r '.id // empty' /tmp/ukbatch-seed-existing.json)
    else
      id=$(grep -o '"id"[[:space:]]*:[[:space:]]*"[^"]*"' /tmp/ukbatch-seed-existing.json \
        | head -n1 | sed 's/.*"id"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/')
    fi
    if [ -z "${id}" ]; then
      echo "!! 409 but could not read the stored definition's id by name '${name}'. Aborting." >&2
      exit 1
    fi
    curl -sS -o /dev/null -X DELETE "${BASE}/batches/by-id/${id}"
    http=$(curl -sS -o /tmp/ukbatch-seed-create.json -w '%{http_code}' \
      -X POST "${BASE}/batches" \
      -H 'Content-Type: application/json' \
      -d "${body}")
    echo "    HTTP ${http} (recreated)"
    cat /tmp/ukbatch-seed-create.json; echo
    if [ "${http}" != "201" ]; then
      echo "!! Recreate after replacing the stale definition failed (expected 201). Aborting." >&2
      exit 1
    fi
  elif [ "${http}" != "201" ]; then
    echo "!! Create failed (expected 201 Created or 409 Conflict). Aborting." >&2
    exit 1
  fi
}

# ─────────────────────────────────────────────────────────────────────────────────────────────────
# trigger_batch <name>
#   POSTs a run by name (empty body). Expects 202 Accepted; the response body carries the batchId.
# ─────────────────────────────────────────────────────────────────────────────────────────────────
trigger_batch() {
  local name="$1" http
  echo
  echo "==> Triggering a run: POST ${BASE}/batches/by-name/${name}/run"
  http=$(curl -sS -o /tmp/ukbatch-seed-trigger.json -w '%{http_code}' \
    -X POST "${BASE}/batches/by-name/${name}/run" \
    -H 'Content-Type: application/json' \
    -d '{}')
  echo "    HTTP ${http}"
  cat /tmp/ukbatch-seed-trigger.json; echo
  if [ "${http}" != "202" ]; then
    echo "!! Trigger failed (expected 202 Accepted). Aborting." >&2
    exit 1
  fi
}

# ─────────────────────────────────────────────────────────────────────────────────────────────────
# Demo 1 — simple two-step sequential cross-service batch.
# ─────────────────────────────────────────────────────────────────────────────────────────────────
seed_simple_demo() {
  local name="worker-mode-demo"
  create_batch "${name}" '{
    "name": "'"${name}"'",
    "source": "Api",
    "failurePolicy": "StopOnFailure",
    "steps": [
      {
        "stepId": "step-1-invoice",
        "order": 0,
        "stepType": "Job",
        "job": { "jobName": "GenerateInvoice", "targetService": "invoicing" }
      },
      {
        "stepId": "step-2-ship",
        "order": 1,
        "stepType": "Job",
        "job": { "jobName": "ShipOrder", "targetService": "shipping" }
      },
      {
        "stepId": "step-3-notify",
        "order": 2,
        "stepType": "Job",
        "job": { "jobName": "SendNotification", "targetService": "notification" }
      }
    ]
  }'
  trigger_batch "${name}"
  echo
  echo "==> Simple demo triggered. The response body carries the batchId; check its status with:"
  echo "    curl -sS ${BASE}/batches/<batchId>/status | jq"
  echo "    (or watch the run live at http://localhost:5070/dashboard/self)"
}

# ─────────────────────────────────────────────────────────────────────────────────────────────────
# Demo 2 — parallel{invoice, ship} -> approval gate -> notify (all three workers).
# ─────────────────────────────────────────────────────────────────────────────────────────────────
seed_approval_parallel_demo() {
  local name="approval-parallel-demo"
  create_batch "${name}" '{
    "name": "'"${name}"'",
    "source": "Api",
    "failurePolicy": "StopOnFailure",
    "steps": [
      {
        "stepId": "step-1-parallel",
        "order": 0,
        "stepType": "ParallelGroup",
        "parallelGroup": {
          "joinPolicy": "WaitAll",
          "steps": [
            {
              "stepId": "step-1a-invoice",
              "order": 0,
              "stepType": "Job",
              "job": { "jobName": "GenerateInvoice", "targetService": "invoicing" }
            },
            {
              "stepId": "step-1b-notify",
              "order": 1,
              "stepType": "Job",
              "job": { "jobName": "SendNotification", "targetService": "notification" }
            }
          ]
        }
      },
      {
        "stepId": "step-2-approve",
        "order": 1,
        "stepType": "ApprovalGate",
        "approval": {
          "title": "Release the cross-service run",
          "description": "The invoice + notify steps have completed; grant to fire the final ship.",
          "allowedRoles": ["ops"],
          "onTimeout": "Fail"
        }
      },
      {
        "stepId": "step-3-ship",
        "order": 2,
        "stepType": "Job",
        "job": { "jobName": "ShipOrder", "targetService": "shipping" }
      }
    ]
  }'
  trigger_batch "${name}"

  echo
  echo "==> approval-parallel-demo triggered. The two parallel jobs (invoice + notify) run FIRST, then"
  echo "    the run PAUSES at the approval gate."
  echo "    Watch it live: http://localhost:5070/dashboard/self/batches (open the run; once the two"
  echo "    parallel nodes go green, the approval node is amber/awaiting). It will NOT proceed to the"
  echo "    final notify until you grant the gate."
  echo
  echo "==> Fetching the pending approval id (GET ${BASE}/approvals):"
  curl -sS "${BASE}/approvals" -o /tmp/ukbatch-seed-approvals.json -w '    HTTP %{http_code}\n'
  cat /tmp/ukbatch-seed-approvals.json; echo

  # Pull the first pending approval id. jq if present, else a grep/sed fallback (no hard jq dep).
  local approval_id=""
  if command -v jq >/dev/null 2>&1; then
    approval_id=$(jq -r '.items[0].approvalId // empty' /tmp/ukbatch-seed-approvals.json)
  else
    approval_id=$(grep -o '"approvalId"[[:space:]]*:[[:space:]]*"[^"]*"' /tmp/ukbatch-seed-approvals.json \
      | head -n1 | sed 's/.*"approvalId"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/')
  fi

  echo
  if [ -n "${approval_id}" ]; then
    echo "==> Pending approval id: ${approval_id}"
    echo "    GRANT it (the ops role header is what lets DevAuth authorize this — UKBATCH_DEV_AUTH must"
    echo "    be true on the server; docker-compose sets it):"
    echo
    echo "    curl -X POST \"${BASE}/approvals/${approval_id}/approve\" \\"
    echo "      -H 'Content-Type: application/json' \\"
    echo "      -H 'X-Dev-User: demo-operator' \\"
    echo "      -H 'X-Dev-Roles: ops' \\"
    echo "      -d '{}'"
    echo
    echo "    The two parallel nodes (invoice + notify) already ran BLUE -> GREEN before the gate; after"
    echo "    granting, step 3 (ship) runs — reading the invoiceId forwarded from the parallel invoice step."
    echo "    The browser approve button can't inject the role header, so curl is the approval path here."
  else
    echo "!! Could not read a pending approval id from ${BASE}/approvals."
    echo "   The gate may not be pending yet (give the run a second and re-run GET ${BASE}/approvals),"
    echo "   or the run already advanced. Grant manually once you have an id:"
    echo "    curl -X POST \"${BASE}/approvals/<approvalId>/approve\" \\"
    echo "      -H 'X-Dev-User: demo-operator' -H 'X-Dev-Roles: ops' -d '{}'"
  fi
}

seed_simple_demo
echo
echo "═════════════════════════════════════════════════════════════════════════════════════════════"
echo
seed_approval_parallel_demo
