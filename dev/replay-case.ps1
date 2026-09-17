#Requires -Version 7.0
<#
.SYNOPSIS
  Replay a case event stream from a local data root and compare outcomes.
.DESCRIPTION
  Loads cases/{caseId}/events.jsonl and record.json, prints a reconstruction
  summary, and optionally writes a replay report under the run directory.
#>
param(
  [Parameter(Mandatory = $true)][string]$CaseId,
  [string]$DataRoot = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot "..")).Path ".dev-data"),
  [string]$RunId = "",
  [switch]$WriteReport
)

$ErrorActionPreference = "Stop"

$caseDir = Join-Path $DataRoot "cases" $CaseId
$recordPath = Join-Path $caseDir "record.json"
$eventsPath = Join-Path $caseDir "events.jsonl"
if (-not (Test-Path $recordPath)) { throw "Case record not found: $recordPath" }
if (-not (Test-Path $eventsPath)) { throw "Case events not found: $eventsPath" }

$record = Get-Content $recordPath -Raw | ConvertFrom-Json
$events = Get-Content $eventsPath | ForEach-Object { $_ | ConvertFrom-Json }

Write-Host "Case $CaseId"
Write-Host "  status:  $($record.status)"
Write-Host "  version: $($record.version)"
Write-Host "  origin:  $($record.origin)"
Write-Host "  kind:    $($record.kind)"
Write-Host "  events:  $($events.Count)"
$events | ForEach-Object {
  "{0}  v{1}  {2}" -f $_.ts, $_.caseVersionAfter, $_.type
}

if ($WriteReport) {
  if (-not $RunId) {
    $pointerPath = Join-Path $DataRoot ".dev-runs" "CURRENT"
    if (Test-Path $pointerPath) { $RunId = (Get-Content $pointerPath -Raw | ConvertFrom-Json).runId }
  }
  if (-not $RunId) { throw "RunId required when -WriteReport and no CURRENT pointer" }
  $out = Join-Path $DataRoot ".dev-runs" $RunId "replay-$CaseId.json"
  @{
    caseId = $CaseId
    record = $record
    eventCount = $events.Count
    eventTypes = @($events | ForEach-Object { $_.type })
    replayedAt = (Get-Date).ToUniversalTime().ToString("o")
  } | ConvertTo-Json -Depth 8 | Set-Content $out -Encoding utf8
  Write-Host "Wrote $out"
}
