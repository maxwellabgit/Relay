#Requires -Version 7.0
<#
.SYNOPSIS
  Tail runtime.jsonl for the current (or specified) local run.
#>
param(
  [string]$DataRoot = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot "..")).Path ".dev-data"),
  [string]$RunId = "",
  [string]$RunDir = "",
  [int]$Tail = 50,
  [switch]$Follow
)

$ErrorActionPreference = "Stop"
$pointerPath = Join-Path $DataRoot ".dev-runs" "CURRENT"

if (-not $RunDir) {
  if ($RunId) {
    $RunDir = Join-Path $DataRoot ".dev-runs" $RunId
  }
  elseif (Test-Path $pointerPath) {
    $ptr = Get-Content $pointerPath -Raw | ConvertFrom-Json
    if ($ptr.runDir) {
      $RunDir = [string]$ptr.runDir
    }
    elseif ($ptr.runId) {
      $RunDir = Join-Path $DataRoot ".dev-runs" $ptr.runId
    }
  }
}

if (-not $RunDir) { throw "No CURRENT run pointer under $DataRoot and no -RunDir/-RunId supplied" }

$log = Join-Path $RunDir "runtime.jsonl"
if (-not (Test-Path $log)) { throw "Missing $log" }

Write-Host "Tailing $log"
if ($Follow) {
  Get-Content $log -Tail $Tail -Wait
} else {
  Get-Content $log -Tail $Tail
}
