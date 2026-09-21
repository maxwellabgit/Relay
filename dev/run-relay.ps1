#Requires -Version 7.0
<#
.SYNOPSIS
  Start the Stage 1 Tauri + Expo RELAY workbench.
.DESCRIPTION
  Creates an isolated run directory under ./runs, writes a pointer, and launches
  `npm run dev:desktop` (Tauri 2 wrapping the shared Expo Web UI).
#>
param(
  [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"
Set-Location $RepoRoot

$env:PATH = "$env:USERPROFILE\.cargo\bin;$env:PATH"

$runId = "run_" + (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmss")
$runDir = Join-Path $RepoRoot "runs" $runId
New-Item -ItemType Directory -Force -Path $runDir,
  (Join-Path $runDir "artifacts"),
  (Join-Path $runDir "screenshots"),
  (Join-Path $runDir "halo") | Out-Null

$gitCommit = (git -C $RepoRoot rev-parse HEAD 2>$null)
$gitDirty = [bool](git -C $RepoRoot status --porcelain 2>$null)

[ordered]@{
  schemaVersion = 1
  runId = $runId
  gitCommit = "$gitCommit"
  gitDirty = $gitDirty
  appVersion = "0.1.0"
  protocolVersion = "1"
  os = "win32"
  startedAt = (Get-Date).ToUniversalTime().ToString("o")
  mode = "tauri-expo"
} | ConvertTo-Json | Set-Content -Path (Join-Path $runDir "manifest.json") -Encoding utf8

Set-Content -Path (Join-Path $RepoRoot "runs" "CURRENT") -Value $runDir -Encoding utf8
$env:RELAY_RUN_ID = $runId
$env:RELAY_RUN_DIR = $runDir

Write-Host "Run $runId"
Write-Host "  runDir: $runDir"
Write-Host "  Starting Tauri workbench (npm run dev:desktop)..."

npm run dev:desktop
