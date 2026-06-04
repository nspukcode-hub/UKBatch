#!/usr/bin/env bash
set -euo pipefail
BASE="${1:-http://localhost:5000/api}"

echo "==> List batches"
curl -fsSL "$BASE/batches" | jq .

echo "==> Get the invoice-pipeline by name"
curl -fsSL "$BASE/batches/by-name/invoice-pipeline" | jq .

echo "==> Run the invoice-pipeline by name"
RESP=$(curl -fsSL -X POST "$BASE/batches/by-name/invoice-pipeline/run" \
    -H "Content-Type: application/json" \
    -d '{}')
echo "$RESP" | jq .
BATCH_ID=$(echo "$RESP" | jq -r .batchId)

echo "==> Query the batch run status (id=$BATCH_ID)"
curl -fsSL "$BASE/batches/$BATCH_ID/status" | jq .
