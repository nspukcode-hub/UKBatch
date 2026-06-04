#!/usr/bin/env bash
set -euo pipefail
BASE="${1:-http://localhost:5000/api}"

echo "==> 404 unknown job"
curl --fail-with-body "$BASE/jobs/does-not-exist" || true

echo
echo "==> 400 invalid limit"
curl --fail-with-body "$BASE/jobs?limit=99999" || true

echo
echo "==> 404 unknown batch by name"
curl --fail-with-body "$BASE/batches/by-name/no-such-batch" || true

echo
echo "==> 403 approval role mismatch (anonymous user)"
APPROVAL_ID=$(curl -fsSL "$BASE/approvals" | jq -r '.items[0].approvalId // empty')
if [ -n "$APPROVAL_ID" ]; then
    curl --fail-with-body -X POST "$BASE/approvals/$APPROVAL_ID/approve" \
        -H "Content-Type: application/json" -d '{}' || true
else
    echo "(no pending approvals to demonstrate against)"
fi
