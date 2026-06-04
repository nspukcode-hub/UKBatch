#!/usr/bin/env bash
set -euo pipefail
BASE="${1:-http://localhost:5000/api}"

echo "==> List pending approvals"
curl -fsSL "$BASE/approvals" | jq .

# Pick the first pending approval (if any).
APPROVAL_ID=$(curl -fsSL "$BASE/approvals" | jq -r '.items[0].approvalId // empty')

if [ -z "$APPROVAL_ID" ]; then
    echo "(no pending approvals to approve)"
    exit 0
fi

echo "==> Approve with DevAuth (X-Dev-User: alice, X-Dev-Roles: ops)"
curl -fsSL -X POST "$BASE/approvals/$APPROVAL_ID/approve" \
    -H "Content-Type: application/json" \
    -H "X-Dev-User: alice" \
    -H "X-Dev-Roles: ops" \
    -d '{"note":"lgtm"}'

echo
echo "(approved)"
