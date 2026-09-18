#Requires -Version 7.0
<#
.SYNOPSIS
  Start an isolated RELAY local run with a unique run ID and diagnostics.
.DESCRIPTION
  Creates or selects an isolated data root, records git commit/dirty state,
  confirms the local model endpoint when -RequireModel is set, starts Relay
  (Desktop on Windows) or the Core DevHarness, writes a run manifest and
  current-run pointer, and streams structured JSONL diagnostics.
#>
param(
  [string]$DataRoot = "",
  [string]$RunId = "",
  [string]$Profile = (Join-Path $PSScriptRoot "profiles/local-ministral.json"),
  [switch]$RequireModel,
  [switch]$HarnessOnly,
  [string]$Scenario = "slice1",
  [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"
Set-Location $RepoRoot

if (-not $DataRoot) {
  $DataRoot = Join-Path $RepoRoot ".dev-data"
}
if (-not $RunId) {
  $RunId = (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmss") + "-" + ([guid]::NewGuid().ToString("N").Substring(0, 8))
}

New-Item -ItemType Directory -Force -Path $DataRoot | Out-Null
$runDir = Join-Path $DataRoot ".dev-runs" $RunId
New-Item -ItemType Directory -Force -Path $runDir,
  (Join-Path $runDir "payloads"),
  (Join-Path $runDir "screenshots"),
  (Join-Path $runDir "crash") | Out-Null

$gitCommit = (git -C $RepoRoot rev-parse HEAD 2>$null)
$gitDirty = [bool](git -C $RepoRoot status --porcelain 2>$null)
$gitBranch = (git -C $RepoRoot rev-parse --abbrev-ref HEAD 2>$null)

$modelOk = $false
if ($RequireModel) {
  & (Join-Path $PSScriptRoot "start-model.ps1") -Profile $Profile
  if ($LASTEXITCODE -ne 0) { throw "Model endpoint required but not healthy" }
  $modelOk = $true
} else {
  & (Join-Path $PSScriptRoot "start-model.ps1") -Profile $Profile | Out-Null
  $modelOk = ($LASTEXITCODE -eq 0)
}

$manifest = [ordered]@{
  runId = $RunId
  startedAt = (Get-Date).ToUniversalTime().ToString("o")
  dataRoot = (Resolve-Path $DataRoot).Path
  git = @{ commit = $gitCommit; dirty = $gitDirty; branch = $gitBranch }
  profile = $Profile
  modelHealthy = $modelOk
  mode = $(if ($HarnessOnly -or -not $IsWindows) { "dev-harness" } else { "desktop" })
  scenario = $Scenario
}
$manifestPath = Join-Path $runDir "manifest.json"
$manifest | ConvertTo-Json -Depth 6 | Set-Content -Path $manifestPath -Encoding utf8

$pointer = Join-Path $DataRoot ".dev-runs" "CURRENT"
@{ runId = $RunId; runDir = $runDir; dataRoot = (Resolve-Path $DataRoot).Path; pid = $null } |
  ConvertTo-Json | Set-Content -Path $pointer -Encoding utf8

$env:RELAY_DATA_ROOT = (Resolve-Path $DataRoot).Path
$env:RELAY_RUN_ID = $RunId
$env:RELAY_RUN_DIR = $runDir

Write-Host "Run $RunId"
Write-Host "  dataRoot: $env:RELAY_DATA_ROOT"
Write-Host "  runDir:   $runDir"
Write-Host "  manifest: $manifestPath"

if ($HarnessOnly -or -not $IsWindows) {
  Write-Host "Starting Relay.DevHarness ($Scenario)..."
  dotnet run --project (Join-Path $RepoRoot "src/Relay.DevHarness") --no-launch-profile -- `
    --data-root $env:RELAY_DATA_ROOT --run-id $RunId --scenario $Scenario
  exit $LASTEXITCODE
}

$desktop = Join-Path $RepoRoot "src/Relay.Desktop/Relay.Desktop.csproj"
Write-Host "Building and launching Relay.Desktop..."
dotnet build $desktop -c Debug
if ($LASTEXITCODE -ne 0) { throw "Desktop build failed" }

$exe = Get-ChildItem -Path (Join-Path $RepoRoot "src/Relay.Desktop/bin") -Recurse -Filter "Relay.exe" |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $exe) { throw "Relay.exe not found after build" }

$proc = Start-Process -FilePath $exe.FullName -PassThru
$ptr = Get-Content $pointer -Raw | ConvertFrom-Json
$ptr.pid = $proc.Id
$ptr | ConvertTo-Json | Set-Content -Path $pointer -Encoding utf8
Write-Host "Relay PID $($proc.Id). Tail with: ./dev/tail-relay.ps1 -DataRoot `"$DataRoot`""
