#Requires -Version 7.0
<#
.SYNOPSIS
  Optional watcher: on a newly observed problem signature, debounce 10s and run headless Cursor triage.
  Never uses --force.
#>
param(
  [string]$DataRoot = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot "..")).Path ".dev-data"),
  [int]$DebounceSeconds = 10
)

$ErrorActionPreference = "Stop"
$pointerPath = Join-Path $DataRoot ".dev-runs" "CURRENT"
if (-not (Test-Path $pointerPath)) { throw "No CURRENT pointer at $pointerPath" }
$ptr = Get-Content $pointerPath -Raw | ConvertFrom-Json
$runDir = [string]$ptr.runDir
$problemsPath = Join-Path $runDir "problems.jsonl"
$outPath = Join-Path $runDir "cursor-triage.md"
$seen = New-Object 'System.Collections.Generic.HashSet[string]'
$pending = $null
$pendingAt = $null

Write-Host "Watching $problemsPath (debounce ${DebounceSeconds}s). Output: $outPath"

while ($true) {
  if (Test-Path $problemsPath) {
    foreach ($line in Get-Content $problemsPath -ErrorAction SilentlyContinue) {
      if ([string]::IsNullOrWhiteSpace($line)) { continue }
      try {
        $p = $line | ConvertFrom-Json
        $sig = [string]$p.signature
        if ([string]::IsNullOrWhiteSpace($sig)) { continue }
        if ($seen.Add($sig)) {
          $pending = $sig
          $pendingAt = Get-Date
          Write-Host "New problem signature: $sig (debouncing...)" -ForegroundColor Yellow
        }
      }
      catch { }
    }
  }

  if ($pending -and $pendingAt -and ((Get-Date) - $pendingAt).TotalSeconds -ge $DebounceSeconds) {
    $prompt = "Read the current RELAY dogfood report and referenced event ranges. Produce root-cause hypotheses, a deterministic reproduction, likely source files, and the smallest regression test. Do not modify files."
    Write-Host "Running agent triage for '$pending'..." -ForegroundColor Cyan
    $triage = & agent -p --output-format text $prompt 2>&1 | Out-String
    @(
      "# Cursor triage",
      "",
      "signature: `$pending`",
      "at: $((Get-Date).ToUniversalTime().ToString('o'))",
      "",
      $triage
    ) -join "`n" | Set-Content -Path $outPath -Encoding utf8
    Write-Host "Wrote $outPath"
    $pending = $null
    $pendingAt = $null
  }

  Start-Sleep -Seconds 1
}
