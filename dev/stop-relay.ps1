#Requires -Version 7.0
<#
.SYNOPSIS
  Stop the current RELAY local run (Desktop process if recorded).
  Does not delete the data root unless -Reset is specified.
#>
param(
  [string]$DataRoot = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot "..")).Path ".dev-data"),
  [switch]$Reset
)

$ErrorActionPreference = "Stop"
$pointerPath = Join-Path $DataRoot ".dev-runs" "CURRENT"
if (-not (Test-Path $pointerPath)) {
  Write-Warning "No CURRENT run pointer"
} else {
  $ptr = Get-Content $pointerPath -Raw | ConvertFrom-Json
  if ($ptr.pid) {
    $proc = Get-Process -Id $ptr.pid -ErrorAction SilentlyContinue
    if ($proc) {
      Write-Host "Stopping PID $($ptr.pid)..."
      Stop-Process -Id $ptr.pid -Force
    } else {
      Write-Host "PID $($ptr.pid) not running"
    }
  }
  Remove-Item $pointerPath -Force
  Write-Host "Cleared CURRENT pointer. Data root preserved at $DataRoot"
}

if ($Reset) {
  Write-Warning "Resetting data root $DataRoot"
  Remove-Item -Recurse -Force $DataRoot
}
