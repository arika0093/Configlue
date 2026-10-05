#!/usr/bin/env bash
# SparseFragments NativeAOT diagnostic gate (#317).
#
# Usage: assert-sparse-aot-diagnostics.sh <publish-log>
#
# Fails on any IL trimming/AOT diagnostic in the publish log. The standalone
# SparseFragments consumer is expected to be fully warning-clean (generated
# fragment converters plus an explicit source-generated resolver path with no
# reflection fallback), so there is intentionally no allowlist here: any
# diagnostic — including any SparseFragments-origin (SparseFragments.*,
# or consumer code) diagnostic — fails the gate.
set -euo pipefail

publish_log="${1:?publish log path required}"
if [[ ! -r "$publish_log" ]]; then
  echo "Publish log is not readable: $publish_log" >&2
  exit 2
fi
unexpected_diagnostics="$(
  grep -E '(warning|error) IL[0-9]{4}:' "$publish_log" \
    || true
)"
if [[ -n "$unexpected_diagnostics" ]]; then
  echo "Unexpected NativeAOT or trimming diagnostics:" >&2
  echo "$unexpected_diagnostics" >&2
  exit 1
fi
echo "SparseFragments NativeAOT diagnostics are clean."
