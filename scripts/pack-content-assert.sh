#!/usr/bin/env bash
# Asserts that the packed UKBatch.Dashboard NuGet package ships its build-time guard for the
# RequiresAspNetWebAssets host property.
#
# A .NET 10 host that consumes UKBatch.Dashboard as a NuGet library must set
# <RequiresAspNetWebAssets>true</RequiresAspNetWebAssets> in its own project, otherwise the Web SDK
# omits _framework/blazor.web.js and the dashboard has no interactivity. That property is read during
# NuGet restore, before a package's build assets are imported, so the package cannot supply it
# automatically — instead it ships a build target that warns (UKBATCH001) when the property is missing.
# This script verifies that target is actually inside the package, so a packaging regression cannot
# silently drop the only safety net the consumer gets.
#
# Usage: scripts/pack-content-assert.sh [feed-dir]   (default ./artifacts)
set -euo pipefail

FEED="${1:-./artifacts}"
PKG=$(ls "$FEED"/UKBatch.Dashboard.*.nupkg 2>/dev/null | grep -v '\.snupkg$' | head -1 || true)
if [ -z "$PKG" ]; then
  echo "FAIL: no UKBatch.Dashboard.*.nupkg found in $FEED"
  exit 1
fi

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
unzip -o "$PKG" -d "$TMP" >/dev/null

TARGETS="$TMP/buildTransitive/UKBatch.Dashboard.targets"
if [ ! -f "$TARGETS" ]; then
  echo "FAIL: buildTransitive/UKBatch.Dashboard.targets is missing from $(basename "$PKG")"
  echo "      Consumers would get no warning when RequiresAspNetWebAssets is unset."
  exit 1
fi
if ! grep -q "UKBATCH001" "$TARGETS"; then
  echo "FAIL: the UKBATCH001 warning is missing from the packed build target"
  exit 1
fi
if ! grep -q "RequiresAspNetWebAssets" "$TARGETS"; then
  echo "FAIL: the build target no longer references RequiresAspNetWebAssets"
  exit 1
fi

echo "PASS: $(basename "$PKG") ships buildTransitive/UKBatch.Dashboard.targets with the UKBATCH001 guard"
