#!/usr/bin/env bash
set -euo pipefail
BASE="${1:-http://localhost:5000/api}"

echo "==> List jobs"
curl -fsSL "$BASE/jobs" | jq .

echo "==> Get a single job"
curl -fsSL "$BASE/jobs/Sample.RestApi.Jobs.InvoiceGenerationJob" | jq .

echo "==> Trigger the invoice job"
curl -fsSL -X POST "$BASE/jobs/Sample.RestApi.Jobs.InvoiceGenerationJob/trigger" \
    -H "Content-Type: application/json" \
    -d '{"parameters":{"month":"2026-05"}}' | jq .
