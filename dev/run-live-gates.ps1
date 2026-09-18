# Windows live-gate preflight for RELAY Conversation-to-Action alpha.
# Reports SKIPPED (exit 3) when secrets/endpoints are absent. Never reports pass for a missing environment.
#
# Usage:
#   pwsh -File dev/run-live-gates.ps1
#   pwsh -File dev/run-live-gates.ps1 -DataRoot .dev-runs/live-gates

param(
    [string]$DataRoot = "",
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
if (-not $DataRoot) {
    $DataRoot = Join-Path $repo ".dev-runs\live-gates-$(Get-Date -Format 'yyyyMMddHHmmss')"
}

$dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

$env:PATH = "$(Split-Path $dotnet);$env:PATH"
New-Item -ItemType Directory -Force -Path $DataRoot | Out-Null

function Write-GateResult([string]$name, [string]$status, [string]$detail) {
    $line = "{0}`t{1}`t{2}" -f $name, $status, $detail
    Write-Host $line
    Add-Content -Path (Join-Path $DataRoot "live-gates.tsv") -Value $line
}

$skipped = 0
$failed = 0
$passed = 0

# --- Preflight: TypeSafe ---
$keyPresent = -not [string]::IsNullOrWhiteSpace($env:TYPESAFE_API_KEY)
if (-not $keyPresent) {
    Write-GateResult "typesafe-preflight" "SKIPPED" "missing TYPESAFE_API_KEY"
    $skipped++
} else {
    Write-GateResult "typesafe-preflight" "READY" "TYPESAFE_API_KEY present"
}

# --- Preflight: local model endpoint ---
$endpoint = $env:RELAY_MODEL_ENDPOINT
if ([string]::IsNullOrWhiteSpace($endpoint)) {
    Write-GateResult "local-model-preflight" "SKIPPED" "missing RELAY_MODEL_ENDPOINT"
    $skipped++
} else {
    try {
        $uri = [Uri]$endpoint
        Write-GateResult "local-model-preflight" "READY" $uri.AbsoluteUri
    } catch {
        Write-GateResult "local-model-preflight" "FAILED" "invalid RELAY_MODEL_ENDPOINT"
        $failed++
    }
}

# --- Preflight: Wispr Flow process (optional signal) ---
$wispr = Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.ProcessName -match 'Wispr|Flow'
} | Select-Object -First 1
if ($null -eq $wispr) {
    Write-GateResult "wispr-preflight" "SKIPPED" "no Wispr Flow process detected"
    $skipped++
} else {
    Write-GateResult "wispr-preflight" "READY" $wispr.ProcessName
}

# --- Build Core + DevHarness ---
& $dotnet build (Join-Path $repo "src\Relay.DevHarness\Relay.DevHarness.csproj") -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    Write-GateResult "build" "FAILED" "DevHarness build failed"
    exit 1
}
Write-GateResult "build" "PASSED" "DevHarness built"

# --- Deterministic Jev scenarios (always required) ---
$scenarios = @("jev-scripted", "jev-outage", "jev-privacy", "jev-improvement")
foreach ($scenario in $scenarios) {
    $runId = "$scenario-$(Get-Date -Format 'HHmmss')"
    & $dotnet run --project (Join-Path $repo "src\Relay.DevHarness\Relay.DevHarness.csproj") -c $Configuration --no-build -- `
        --data-root $DataRoot --run-id $runId --scenario $scenario
    if ($LASTEXITCODE -eq 0) {
        Write-GateResult $scenario "PASSED" "exit 0"
        $passed++
    } else {
        Write-GateResult $scenario "FAILED" "exit $LASTEXITCODE"
        $failed++
    }
}

# --- Live TypeSafe smoke ---
$runId = "jev-live-$(Get-Date -Format 'HHmmss')"
& $dotnet run --project (Join-Path $repo "src\Relay.DevHarness\Relay.DevHarness.csproj") -c $Configuration --no-build -- `
    --data-root $DataRoot --run-id $runId --scenario jev-live
$code = $LASTEXITCODE
if ($code -eq 3) {
    Write-GateResult "jev-live" "SKIPPED" "missing TYPESAFE_API_KEY or documented skip"
    $skipped++
} elseif ($code -eq 0) {
    Write-GateResult "jev-live" "PASSED" "live TypeSafe smoke"
    $passed++
} else {
    Write-GateResult "jev-live" "FAILED" "exit $code"
    $failed++
}

Write-Host ""
Write-Host "live-gates summary: passed=$passed skipped=$skipped failed=$failed"
Write-Host "results: $(Join-Path $DataRoot 'live-gates.tsv')"

if ($failed -gt 0) { exit 1 }
# Exit 3 when anything was skipped so CI/host knows live proof is incomplete.
if ($skipped -gt 0) { exit 3 }
exit 0
