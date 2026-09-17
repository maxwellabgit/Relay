#Requires -Version 7.0
<#
.SYNOPSIS
  Tail runtime.jsonl for the current (or specified) local run.
#>
param(
  [string]$DataRoot = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot "..")).Path ".dev-data"),
  [string]$RunId = "",
  [int]$Tail = 50,
  [switch]$Follow
)

$ErrorActionPreference = "Stop"
$pointerPath = Join-Path $DataRoot ".dev-runs" "CURRENT"
if (-not $RunId) {
  if (-not (Test-Path $pointerPath)) { throw "No CURRENT run pointer under $DataRoot" }
  $RunId = (Get-Content $pointerPath -Raw | ConvertFrom-Json).runId
}

$log = Join-Path $DataRoot ".dev-runs" $RunId "runtime.jsonl"
if (-not (Test-Path $log)) { throw "Missing $log" }

Write-Host "Tailing $log"
if ($Follow) {
  Get-Content $log -Tail $Tail -Wait
} else {
  Get-Content $log -Tail $Tail
}
