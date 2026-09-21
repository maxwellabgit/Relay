<#
.SYNOPSIS
  Diagnostic Windows V1 readiness gate (no secrets printed).

.DESCRIPTION
  Runs required structural checks for the TypeScript/Tauri Windows path.
  Exits nonzero when a required check fails. Does not claim dogfood or NSIS install passed.
  Compatible with Windows PowerShell 5.1 and PowerShell 7+.
#>
param(
  [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"
Set-Location $RepoRoot

$failed = 0
$results = @()

function Write-Check([string]$Name, [bool]$Ok, [string]$Detail) {
  $script:results += [pscustomobject]@{ name = $Name; ok = $Ok; detail = $Detail }
  if ($Ok) {
    Write-Host "ok  $Name — $Detail"
  } else {
    Write-Host "FAIL $Name — $Detail" -ForegroundColor Red
    $script:failed += 1
  }
}

Write-Host "RELAY Windows V1 readiness (diagnostic)"
Write-Host "repo: $RepoRoot"
Write-Host ""

# Branch / baseline awareness
$branch = (git -C $RepoRoot rev-parse --abbrev-ref HEAD 2>$null)
$head = (git -C $RepoRoot rev-parse --short HEAD 2>$null)
Write-Check "git" ($LASTEXITCODE -eq 0 -and $head) "branch=$branch head=$head"

# Required source files from wrap pass
$requiredPaths = @(
  "packages/storage-schema/migrations/008_protect_legacy_content.sql",
  "adapters/node/src/migrate-legacy-protected.ts",
  "packages/engine/src/jev-health.ts",
  "docs/STATUS.md",
  "docs/WINDOWS_V1_ACCEPTANCE.md",
  "apps/desktop/src-tauri/src/state.rs",
  "apps/relay/src/bootstrap/createDesktopClient.ts"
)
foreach ($rel in $requiredPaths) {
  $full = Join-Path $RepoRoot $rel
  Write-Check "path:$rel" (Test-Path -LiteralPath $full) $(if (Test-Path -LiteralPath $full) { "present" } else { "missing" })
}

# Typecheck (required)
Write-Host ""
Write-Host "Running npm run typecheck..."
$tc = & npm run typecheck 2>&1
$tcOk = $LASTEXITCODE -eq 0
Write-Check "typecheck" $tcOk $(if ($tcOk) { "passed" } else { "failed — see npm output" })
if (-not $tcOk) {
  $tc | Select-Object -Last 40 | ForEach-Object { Write-Host $_ }
}

# Focused automated suites (required for readiness signal)
Write-Host ""
Write-Host "Running focused unit/integration/privacy/replay suites..."
$suites = @(
  @{ name = "unit:hosted+jev"; args = @("run", "test:unit", "--", "packages/engine/src/hosted-processing.unit.test.ts", "packages/engine/src/jev-health.unit.test.ts") },
  @{ name = "integration:listen"; args = @("run", "test:integration", "--", "adapters/node/src/listen-intake.integration.test.ts") },
  @{ name = "privacy:legacy+protected"; args = @("run", "test:privacy", "--", "adapters/node/src/legacy-protected-migration.privacy.test.ts", "adapters/node/src/protected-learning.privacy.test.ts") },
  @{ name = "replay"; args = @("run", "test:replay") }
)
foreach ($suite in $suites) {
  $out = & npm @($suite.args) 2>&1
  $ok = $LASTEXITCODE -eq 0
  Write-Check $suite.name $ok $(if ($ok) { "passed" } else { "failed" })
  if (-not $ok) {
    $out | Select-Object -Last 25 | ForEach-Object { Write-Host $_ }
  }
}

# Optional environment hints (non-failing)
Write-Host ""
Write-Host "Optional environment (informational only):"
$modelPort = if ($env:RELAY_LOCAL_MODEL_PORT) { $env:RELAY_LOCAL_MODEL_PORT } else { "8080" }
try {
  $resp = Invoke-WebRequest -Uri "http://127.0.0.1:$modelPort/v1/models" -TimeoutSec 2 -UseBasicParsing
  Write-Host "  local model : reachable on :$modelPort (status $($resp.StatusCode))"
} catch {
  Write-Host "  local model : not reachable on :$modelPort (Ask may show external:unavailable)"
}

$audioDoctor = Join-Path $RepoRoot "tools/audio"
if (Test-Path $audioDoctor) {
  Write-Host "  audio tools : present under tools/audio (run python -m relay_audio.doctor separately)"
} else {
  Write-Host "  audio tools : missing tools/audio"
}

Write-Host "  secrets     : not inspected (never printed by this gate)"
Write-Host "  dogfood/NSIS: not claimed — see docs/WINDOWS_V1_ACCEPTANCE.md"

Write-Host ""
if ($failed -gt 0) {
  Write-Host "readiness:windows FAILED ($failed required check(s))" -ForegroundColor Red
  exit 1
}
Write-Host "readiness:windows passed (diagnostic; manual dogfood still separate)"
exit 0
