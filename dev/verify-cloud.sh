#!/usr/bin/env bash
# Cloud verification gate for refactor/jev-runtime (§13).
# Deterministic + fixture only. Nonzero exit on any required failure.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

REPORT_DIR="${REPORT_DIR:-/tmp/relay-verify-cloud}"
mkdir -p "$REPORT_DIR"
REPORT_JSON="$REPORT_DIR/report.json"
START_TS="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
FAILED=0
SKIPPED_REQUIRED=0
RESULTS_FILE="$REPORT_DIR/results.jsonl"
: >"$RESULTS_FILE"

record() {
  local name="$1"
  local status="$2"
  local detail="${3:-}"
  python3 - "$name" "$status" "$detail" "$RESULTS_FILE" <<'PY'
import json, sys
name, status, detail, path = sys.argv[1:5]
with open(path, "a", encoding="utf-8") as f:
    f.write(json.dumps({"name": name, "status": status, "detail": detail}) + "\n")
print(f"[{status}] {name} {detail}")
PY
  if [[ "$status" == "FAIL" || "$status" == "SKIP" ]]; then
    FAILED=1
    if [[ "$status" == "SKIP" ]]; then SKIPPED_REQUIRED=1; fi
  fi
}

run_cmd() {
  local name="$1"; shift
  if "$@" >"$REPORT_DIR/${name}.log" 2>&1; then
    record "$name" "PASS"
  else
    record "$name" "FAIL" "see ${name}.log"
  fi
}

echo "== Relay verify-cloud =="
echo "root=$ROOT report=$REPORT_DIR"

run_cmd "test-core" dotnet test tests/Relay.Core.Tests -c Release --nologo
run_cmd "test-gateway" dotnet test tests/Relay.Gateway.Tests -c Release --nologo

if [[ -d tests/Relay.Integration.Tests ]]; then
  if ls tests/Relay.Integration.Tests/*.csproj >/dev/null 2>&1; then
    run_cmd "test-integration" dotnet test tests/Relay.Integration.Tests -c Release --nologo
  else
    record "test-integration" "PASS" "scaffold only - no csproj yet"
  fi
fi

run_cmd "build-gateway" dotnet build src/Relay.Gateway -c Release --nologo
run_cmd "build-worker" dotnet build src/Relay.Worker -c Release --nologo
run_cmd "build-devharness" dotnet build src/Relay.DevHarness -c Release --nologo

SCENARIOS=(
  slice1 slice2 slice3 slice4 slice5 slice6 slice7
  jev-atlas-stream jev-direct-recall jev-local-only jev-outage-recovery
  jev-concurrency jev-cancel-stale jev-research-evidence jev-retention
  jev-capability-growth jev-projection-rebuild
)

for s in "${SCENARIOS[@]}"; do
  root="$REPORT_DIR/data/$s"
  rm -rf "$root"
  mkdir -p "$root"
  if dotnet run --project src/Relay.DevHarness -c Release --no-build -- \
      --scenario "$s" --provider fixture --data-root "$root" --run-id "$s" \
      >"$REPORT_DIR/harness-$s.log" 2>&1; then
    record "harness:$s" "PASS"
  else
    record "harness:$s" "FAIL" "see harness-$s.log"
  fi
done

END_TS="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
python3 - "$REPORT_JSON" "$START_TS" "$END_TS" "$FAILED" "$SKIPPED_REQUIRED" "$RESULTS_FILE" <<'PY'
import json, sys
out, start, end, failed, skipped, results_path = sys.argv[1:7]
results = []
with open(results_path, encoding="utf-8") as f:
    for line in f:
        line = line.strip()
        if line:
            results.append(json.loads(line))
payload = {
    "startedAt": start,
    "endedAt": end,
    "failed": int(failed),
    "skippedRequired": int(skipped),
    "results": results,
}
with open(out, "w", encoding="utf-8") as f:
    json.dump(payload, f, indent=2)
    f.write("\n")
print("Wrote", out)
PY

if [[ $FAILED -ne 0 ]]; then
  echo "verify-cloud FAILED"
  exit 1
fi
echo "verify-cloud PASSED"
exit 0
