#!/usr/bin/env bash
# Supported NativeAOT diagnostic gate (#208, #251).
#
# Usage: assert-supported-aot-diagnostics.sh <publish-log>
#
# Fails unless every IL trimming/AOT diagnostic in the publish log is one of the
# two proven-unreachable upstream ones documented below. There is intentionally no
# warning-code or namespace allowlist here: any other diagnostic — including any
# new diagnostic pointing at Configlue or consumer code — fails the gate.
#
# The only permitted diagnostics are IL2060/IL3050 on
# MessagePack.MessagePackSecurity.ObjectFallbackEqualityComparer.GetHashCode.
# MessagePack 3.1.11 instantiates that comparer from every MessagePackSecurity
# (reached via `new MessagePackSerializerOptions`), so ILC analyzes the method
# even though it is never invoked from the closed-world path: its only callers
# are the dictionary/set formatter GetEqualityComparer paths, none of which are
# referenced (verified by IL-level call-graph analysis of MessagePack 3.1.11;
# elimination of MessagePackSerializer statics, BuiltinResolver, and
# StandardResolver leaves exactly these two). ILC still analyzes the method
# because the type is instantiated, so the gate asserts the exact member
# instead of the diagnostic code.
set -euo pipefail

publish_log="${1:?publish log path required}"
if [[ ! -r "$publish_log" ]]; then
  echo "Publish log is not readable: $publish_log" >&2
  exit 2
fi
allowed='ILC : .* (warning|error) IL(2060|3050): MessagePack\.MessagePackSecurity\.ObjectFallbackEqualityComparer\.GetHashCode\(Object\):'
unexpected_diagnostics="$(
  grep -E '(warning|error) IL[0-9]{4}:' "$publish_log" \
    | grep -Ev "$allowed" \
    || true
)"
if [[ -n "$unexpected_diagnostics" ]]; then
  echo "Unexpected NativeAOT or trimming diagnostics:" >&2
  echo "$unexpected_diagnostics" >&2
  exit 1
fi
echo "Supported NativeAOT diagnostics are within the proven residual."
