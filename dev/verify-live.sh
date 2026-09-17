#!/usr/bin/env bash
# Live verification gate (§13). Requires credentials; fails preflight if missing.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

missing=0
if [[ -z "${TYPESAFE_API_KEY:-}" ]]; then
  echo "PREFLIGHT FAIL: TYPESAFE_API_KEY is not set."
  missing=1
fi

# Local model endpoint must be configured for live local jobs.
if [[ -z "${RELAY_LOCAL_MODEL_ENDPOINT:-}" && -z "${LOCAL_MODEL_ENDPOINT:-}" ]]; then
  echo "PREFLIGHT FAIL: RELAY_LOCAL_MODEL_ENDPOINT (or LOCAL_MODEL_ENDPOINT) is not set."
  missing=1
fi

if [[ $missing -ne 0 ]]; then
  echo "verify-live.sh refusing to run without live credentials/endpoints."
  exit 2
fi

echo "Live preflight OK. Running fixture cloud gate first, then live smoke (when wired)."
bash "$ROOT/dev/verify-cloud.sh"

# Placeholder for live Jev smoke once Gateway live client tests are enabled.
echo "Live Jev smoke: set LIVE_JEV=1 to enable additional live calls (not yet expanded)."
if [[ "${LIVE_JEV:-}" == "1" ]]; then
  echo "LIVE_JEV=1 noted — extend with Gateway live transport tests when ready."
fi

exit 0
