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

$script:failed = 0

function Write-Check {
  param(
    [string]$Name,
    [bool]$Ok,
    [string]$Detail
  )
  if ($Ok) {
    Write-Host ("ok  {0} - {1}" -f $Name, $Detail)
  } else {
    Write-Host ("FAIL {0} - {1}" -f $Name, $Detail) -ForegroundColor Red
    $script:failed += 1
  }
}

Write-Host "RELAY Windows V1 readiness (diagnostic)"
Write-Host ("repo: {0}" -f $RepoRoot)
Write-Host ""

$branch = git -C $RepoRoot rev-parse --abbrev-ref HEAD 2>$null
$head = git -C $RepoRoot rev-parse --short HEAD 2>$null
$gitOk = ($LASTEXITCODE -eq 0) -and (-not [string]::IsNullOrWhiteSpace([string]$head))
Write-Check -Name "git" -Ok $gitOk -Detail ("branch={0} head={1}" -f $branch, $head)

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
  $exists = Test-Path -LiteralPath $full
  Write-Check -Name ("path:{0}" -f $rel) -Ok $exists -Detail $(if ($exists) { "present" } else { "missing" })
}

Write-Host ""
Write-Host "Running npm run typecheck..."
npm run typecheck 2>&1 | Out-Null
$tcOk = $LASTEXITCODE -eq 0
Write-Check -Name "typecheck" -Ok $tcOk -Detail $(if ($tcOk) { "passed" } else { "failed - see npm output" })

Write-Host ""
Write-Host "Running focused unit/integration/privacy/replay suites..."
$suites = @(
  @{
    name = "unit:hosted+jev"
    command = "npm run test:unit -- packages/engine/src/hosted-processing.unit.test.ts packages/engine/src/jev-health.unit.test.ts"
  },
  @{
    name = "integration:listen"
    command = "npm run test:integration -- adapters/node/src/listen-intake.integration.test.ts"
  },
  @{
    name = "privacy:legacy+protected"
    command = "npm run test:privacy -- adapters/node/src/legacy-protected-migration.privacy.test.ts adapters/node/src/protected-learning.privacy.test.ts"
  },
  @{
    name = "replay"
    command = "npm run test:replay"
  }
)
foreach ($suite in $suites) {
  $prev = $ErrorActionPreference
  $ErrorActionPreference = "Continue"
  cmd /c $suite.command 1>$null 2>$null
  $code = $LASTEXITCODE
  $ErrorActionPreference = $prev
  $ok = $code -eq 0
  Write-Check -Name $suite.name -Ok $ok -Detail $(if ($ok) { "passed" } else { "failed" })
}

Write-Host ""
Write-Host "Optional environment (informational only):"
$modelPort = if ($env:RELAY_LOCAL_MODEL_PORT) { $env:RELAY_LOCAL_MODEL_PORT } else { "8080" }
try {
  $resp = Invoke-WebRequest -Uri ("http://127.0.0.1:{0}/v1/models" -f $modelPort) -TimeoutSec 2 -UseBasicParsing
  Write-Host ("  local model : reachable on :{0} (status {1})" -f $modelPort, $resp.StatusCode)
} catch {
  Write-Host ("  local model : not reachable on :{0} (Ask may show external:unavailable)" -f $modelPort)
}

$audioDoctor = Join-Path $RepoRoot "tools/audio"
if (Test-Path $audioDoctor) {
  Write-Host "  audio tools : present under tools/audio (run python -m relay_audio.doctor separately)"
} else {
  Write-Host "  audio tools : missing tools/audio"
}

Write-Host "  secrets     : not inspected (never printed by this gate)"
Write-Host "  dogfood/NSIS: not claimed - see docs/WINDOWS_V1_ACCEPTANCE.md"

Write-Host ""
if ($script:failed -gt 0) {
  Write-Host ("readiness:windows FAILED ({0} required check(s))" -f $script:failed) -ForegroundColor Red
  exit 1
}
Write-Host "readiness:windows passed (diagnostic; manual dogfood still separate)"
exit 0
