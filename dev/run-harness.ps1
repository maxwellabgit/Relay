#Requires -Version 7.0
<#
.SYNOPSIS
  Run the cross-platform Core harness scenarios (Slice 1+).
#>
param(
  [string]$DataRoot = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot "..")).Path ".dev-data"),
  [string]$Scenario = "slice1",
  [string]$RunId = ""
)

$ErrorActionPreference = "Stop"
if (-not $RunId) {
  $RunId = "$Scenario-" + (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmss")
}

& (Join-Path $PSScriptRoot "run-relay.ps1") -DataRoot $DataRoot -RunId $RunId -HarnessOnly -Scenario $Scenario
