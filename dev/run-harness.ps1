#Requires -Version 7.0
<#
.SYNOPSIS
  Run the cross-platform Core harness scenarios (Slice 1+).
#>
param(
  [string]$DataRoot = "",
  [string]$Scenario = "slice1",
  [string]$RunId = "",
  [switch]$SharedRoot
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
# Default: isolate each scenario under .dev-data/scenarios/{scenario} so runs do not collide.
if (-not $DataRoot) {
  $DataRoot = if ($SharedRoot) {
    Join-Path $repo ".dev-data"
  } else {
    Join-Path $repo ".dev-data" "scenarios" $Scenario
  }
}
if (-not $RunId) {
  $RunId = "$Scenario-" + (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmss")
}

& (Join-Path $PSScriptRoot "run-relay.ps1") -DataRoot $DataRoot -RunId $RunId -HarnessOnly -Scenario $Scenario
