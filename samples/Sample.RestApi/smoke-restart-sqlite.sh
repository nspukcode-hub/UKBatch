#!/usr/bin/env bash
#
# SQLite restart-persistence smoke (operator-facing, real process kill/restart).
#
# Proves the EF Core adapter's durable-storage promise end-to-end over real HTTP:
#   1. Boot Sample.RestApi with --storage ef-sqlite over a FILE.
#   2. Trigger the wildcard-approval-pipeline (5-min Hold gate → stays pending).
#   3. Wait until the approval gate is pending; record execution history + the pending approval id.
#   4. kill -9 the process (a true crash — no graceful shutdown).
#   5. Restart over the SAME db file.
#   6. Re-query: the pending approval gate AND the execution history must still be there.
#
# The automated, CI-gated complement to this is HostRestartPersistenceTests (cross-host file persistence
# of all three stores). This script is the real-process operator demo — run it, watch it pass.
#
# Usage:  ./smoke-restart-sqlite.sh
# Requires: dotnet 10 SDK, jq, curl.  Docker NOT required.

set -euo pipefail

PORT="${PORT:-5099}"                                  # avoid macOS :5000 AirPlay
BASE="http://localhost:${PORT}/api"
DB="$(mktemp -t ukbatch-smoke-XXXXXX).db"
PIPELINE="wildcard-approval-pipeline"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DLL="${ROOT}/samples/Sample.RestApi/bin/Release/net10.0/Sample.RestApi.dll"

green() { printf '\033[32m%s\033[0m\n' "$1"; }
red()   { printf '\033[31m%s\033[0m\n' "$1"; }
info()  { printf '\033[36m▶ %s\033[0m\n' "$1"; }

APP_PID=""
cleanup() {
  [[ -n "${APP_PID}" ]] && kill -9 "${APP_PID}" 2>/dev/null || true
  rm -f "${DB}" "${DB}-wal" "${DB}-shm" 2>/dev/null || true
}
trap cleanup EXIT

start_app() {
  info "starting Sample.RestApi (storage=ef-sqlite, db=${DB}, port=${PORT})"
  dotnet "${DLL}" \
    --storage ef-sqlite \
    --storage-connection "DataSource=${DB}" \
    --urls "http://localhost:${PORT}" \
    > "/tmp/ukbatch-smoke-app.log" 2>&1 &
  APP_PID=$!
}

wait_for_health() {
  for _ in $(seq 1 40); do
    if curl -fsS "http://localhost:${PORT}/healthz" >/dev/null 2>&1; then return 0; fi
    sleep 0.5
  done
  red "app did not become healthy"; cat /tmp/ukbatch-smoke-app.log; exit 1
}

pending_count()    { curl -fsS "${BASE}/approvals/" | jq '.totalCount // .TotalCount // (.items | length)'; }
pending_first_id() { curl -fsS "${BASE}/approvals/" | jq -r '(.items // .Items)[0] | (.approvalId // .ApprovalId)'; }
history_count()    { curl -fsS -X POST "${BASE}/executions/query" -H 'Content-Type: application/json' -d '{"limit":100}' \
                       | jq '.totalCount // .TotalCount // (.items | length)'; }

# ---- build once ----
info "building Sample.RestApi (Release)"
dotnet build -c Release "${ROOT}/samples/Sample.RestApi" >/tmp/ukbatch-smoke-build.log 2>&1 \
  || { red "build failed"; tail -30 /tmp/ukbatch-smoke-build.log; exit 1; }

# ---- boot #1 ----
start_app
wait_for_health
green "boot #1 healthy"

info "triggering ${PIPELINE}"
curl -fsS -X POST "${BASE}/batches/by-name/${PIPELINE}/run" -H 'Content-Type: application/json' -d '{}' >/dev/null

info "waiting for the approval gate to become pending"
GATE_ID=""
for _ in $(seq 1 40); do
  if [[ "$(pending_count)" -ge 1 ]]; then GATE_ID="$(pending_first_id)"; break; fi
  sleep 0.5
done
[[ -n "${GATE_ID}" ]] || { red "no pending approval gate appeared"; cat /tmp/ukbatch-smoke-app.log; exit 1; }
HIST_BEFORE="$(history_count)"
green "pre-kill state: pending approval id=${GATE_ID}, execution history rows=${HIST_BEFORE}"

# ---- crash ----
info "kill -9 ${APP_PID}  (simulating a crash — no graceful shutdown)"
kill -9 "${APP_PID}"; wait "${APP_PID}" 2>/dev/null || true; APP_PID=""
sleep 1

# ---- boot #2 over the SAME file ----
start_app
wait_for_health
green "boot #2 healthy (same db file)"

PENDING_AFTER="$(pending_count)"
GATE_AFTER="$(pending_first_id)"
HIST_AFTER="$(history_count)"

echo
echo "================ RESULT ================"
printf 'pending approvals : before=1(%s)  after=%s(%s)\n' "${GATE_ID}" "${PENDING_AFTER}" "${GATE_AFTER}"
printf 'execution history : before=%s  after=%s\n' "${HIST_BEFORE}" "${HIST_AFTER}"
echo "========================================"

FAIL=0
[[ "${PENDING_AFTER}" -ge 1 && "${GATE_AFTER}" == "${GATE_ID}" ]] \
  || { red "FAIL: pending approval did not survive the restart"; FAIL=1; }
[[ "${HIST_AFTER}" -ge "${HIST_BEFORE}" && "${HIST_AFTER}" -ge 1 ]] \
  || { red "FAIL: execution history did not survive the restart"; FAIL=1; }

if [[ "${FAIL}" -eq 0 ]]; then
  green "✓ PASS — pending approval + execution history persisted across a real process restart (SQLite file)"
else
  exit 1
fi
