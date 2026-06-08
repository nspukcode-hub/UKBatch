#!/usr/bin/env bash
# End-to-end consumer smoke for UKBatch.Dashboard.
#
# Scaffolds a fresh .NET 10 web host OUTSIDE the repo, installs the freshly packed package from a
# local folder feed using an ISOLATED NuGet cache (so the machine's global cache is never polluted),
# and verifies the dashboard framework-asset contract that a real PackageReference consumer sees —
# which solution-internal ProjectReference hosts and bunit/WebApplicationFactory tests cannot exercise:
#
#   * WITHOUT <RequiresAspNetWebAssets> in the host: the build emits warning UKBATCH001 and
#     _framework/blazor.web.js returns 404 at runtime (the documented host requirement; the asset is
#     resolved during restore from a property the package cannot supply).
#   * WITH the property set: no warning, and the asset returns 200.
#
# The feed must contain UKBatch.Dashboard plus its dependency graph
# (Abstractions, Core, AspNetCore, Api). Produce it with:
#   dotnet pack UKBatch.sln -c Release -o ./artifacts
#
# Usage: scripts/consumer-smoke.sh [feed-dir] [port]   (defaults: ./artifacts, 5099)
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FEED="$(cd "${1:-$REPO_ROOT/artifacts}" && pwd)"
PORT="${2:-5099}"

PKG=$(ls "$FEED"/UKBatch.Dashboard.*.nupkg 2>/dev/null | grep -v '\.snupkg$' | head -1 || true)
[ -n "$PKG" ] || { echo "FAIL: no UKBatch.Dashboard.*.nupkg in $FEED (run: dotnet pack UKBatch.sln -c Release -o ./artifacts)"; exit 1; }
# Derive the version from the package file name: UKBatch.Dashboard.<version>.nupkg
VERSION=$(basename "$PKG" .nupkg | sed 's/^UKBatch.Dashboard\.//')
echo "Using UKBatch.Dashboard $VERSION from $FEED"

WORK=$(mktemp -d)
trap 'kill "${RUN:-}" 2>/dev/null || true; rm -rf "$WORK"' EXIT
# Pin the same SDK the repo builds with.
[ -f "$REPO_ROOT/global.json" ] && cp "$REPO_ROOT/global.json" "$WORK/global.json"

dotnet new webapi -f net10.0 -n SmokeHost -o "$WORK/SmokeHost" >/dev/null
HOST="$WORK/SmokeHost"

cat > "$HOST/nuget.config" <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <config>
    <add key="globalPackagesFolder" value="$WORK/pkgs" />
  </config>
  <packageSources>
    <clear />
    <add key="local" value="$FEED" />
    <add key="nuget" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
XML

cat > "$HOST/Program.cs" <<'CS'
using UKBatch.Api;
using UKBatch.AspNetCore;
using UKBatch.Dashboard;
using UKBatch.Dashboard.Configuration;

var builder = WebApplication.CreateBuilder(args);
builder.AddUKBatchAspNetCore(b => { });
builder.Services.AddUKBatchApi();
builder.Services.AddUKBatchDashboard(opts =>
{
    opts.Services.Add(new UKBatchServiceDescriptor
    {
        Name = "self",
        BaseUrl = new Uri("http://localhost:5099/api/"),
        DisplayName = "Local",
    });
});
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery();
var app = builder.Build();
app.UseAuthorization();
app.UseAntiforgery();
app.MapGroup("/api").MapUKBatchApi();
app.MapUKBatchDashboard();
app.MapStaticAssets();
app.MapHealthChecks("/healthz");
app.Run();
CS

# No --source flag: the nuget.config above already lists the local feed AND nuget.org, so the
# package resolves from the feed while its transitive (non-UKBatch) dependencies resolve from nuget.org.
dotnet add "$HOST/SmokeHost.csproj" package UKBatch.Dashboard --version "$VERSION" >/dev/null

probe() {  # $1 = url -> prints HTTP status
  curl -s -o /dev/null -w '%{http_code}' "$1"
}

fail=0

echo ""
echo "=== Scenario 1: host WITHOUT RequiresAspNetWebAssets (expect UKBATCH001 + 404) ==="
rm -rf "$WORK/pkgs" "$HOST/obj" "$HOST/bin"
BUILD1=$(dotnet build "$HOST/SmokeHost.csproj" -c Release 2>&1)
if echo "$BUILD1" | grep -q "warning UKBATCH001"; then echo "  build warning UKBATCH001: present (good)"; else echo "  build warning UKBATCH001: MISSING (bad)"; fail=1; fi
dotnet run --project "$HOST/SmokeHost.csproj" -c Release --no-build --urls "http://localhost:$PORT" >/dev/null 2>&1 &
RUN=$!
curl -s --retry 40 --retry-delay 1 --retry-connrefused -o /dev/null "http://localhost:$PORT/healthz" || true
S1=$(probe "http://localhost:$PORT/_framework/blazor.web.js")
echo "  blazor.web.js: $S1 (expect 404)"
[ "$S1" = "404" ] || { echo "  unexpected: blazor.web.js should be 404 without the property"; fail=1; }
kill "$RUN" 2>/dev/null || true; wait "$RUN" 2>/dev/null || true

echo ""
echo "=== Scenario 2: host WITH RequiresAspNetWebAssets (expect no warning + 200) ==="
rm -rf "$WORK/pkgs" "$HOST/obj" "$HOST/bin"
BUILD2=$(dotnet build "$HOST/SmokeHost.csproj" -c Release -p:RequiresAspNetWebAssets=true 2>&1)
if echo "$BUILD2" | grep -q "warning UKBATCH001"; then echo "  build warning UKBATCH001: present (bad — should be silent)"; fail=1; else echo "  build warning UKBATCH001: absent (good)"; fi
dotnet run --project "$HOST/SmokeHost.csproj" -c Release --no-build -p:RequiresAspNetWebAssets=true --urls "http://localhost:$PORT" >/dev/null 2>&1 &
RUN=$!
curl -s --retry 40 --retry-delay 1 --retry-connrefused -o /dev/null "http://localhost:$PORT/healthz" || true
S2=$(probe "http://localhost:$PORT/_framework/blazor.web.js")
echo "  blazor.web.js: $S2 (expect 200)"
[ "$S2" = "200" ] || { echo "  unexpected: blazor.web.js should be 200 with the property"; fail=1; }
kill "$RUN" 2>/dev/null || true; wait "$RUN" 2>/dev/null || true

echo ""
if [ "$fail" = "0" ]; then echo "CONSUMER SMOKE: PASS"; else echo "CONSUMER SMOKE: FAIL"; exit 1; fi
