#!/usr/bin/env bash
set -euo pipefail
BASE="${1:-http://localhost:5003/api}"

echo "==> Query executions (most recent 50)"
curl -fsSL -X POST "$BASE/executions/query" \
    -H "Content-Type: application/json" \
    -d '{"limit":50,"descendingByEnqueuedAt":true}' | jq .
