#!/usr/bin/env bash
# Demonstrate auth-on group calls.

set -euo pipefail

BASE_URL="${BASE_URL:-http://localhost:5000}"

# Anonymous call against /api — should succeed (anonymous mount allows GET).
echo "=== Anonymous to /api/batches ==="
curl -s -o /dev/null -w "HTTP %{http_code}\n" "${BASE_URL}/api/batches"

# Anonymous call against /api/secured — should fail with 401 (RequireAuthorization).
echo "=== Anonymous to /api/secured/batches ==="
curl -s -o /dev/null -w "HTTP %{http_code}\n" "${BASE_URL}/api/secured/batches"

# Authenticated call against /api/secured — should succeed with 200.
echo "=== Authenticated (alice + ops) to /api/secured/batches ==="
curl -s -o /dev/null -w "HTTP %{http_code}\n" \
  -H "X-Dev-User: alice" \
  -H "X-Dev-Roles: ops" \
  "${BASE_URL}/api/secured/batches"
