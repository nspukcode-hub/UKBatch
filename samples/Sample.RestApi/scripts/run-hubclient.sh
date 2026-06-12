#!/usr/bin/env bash
set -euo pipefail
HUB_URL="${1:-http://localhost:5003/api/hubs/jobs}"
dotnet run --project samples/Sample.RestApi.HubClient/ -- "$HUB_URL"
