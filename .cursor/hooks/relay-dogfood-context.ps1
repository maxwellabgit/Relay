#Requires -Version 5.1
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$pointer = Join-Path $repoRoot ".dev-data\.dev-runs\CURRENT"
$context = "RELAY dogfood: no CURRENT run pointer under .dev-data/.dev-runs/CURRENT."

if (Test-Path $pointer) {
  try {
    $ptr = Get-Content $pointer -Raw -Encoding UTF8 | ConvertFrom-Json
    $runDir = [string]$ptr.runDir
    $runId = [string]$ptr.runId
    $latest = Join-Path $runDir "latest-problems.md"
    $snippet = ""
    if (Test-Path $latest) {
      $bytes = [System.IO.File]::ReadAllBytes($latest)
      $take = [Math]::Min(12KB, $bytes.Length)
      $snippet = [System.Text.Encoding]::UTF8.GetString($bytes, 0, $take)
    }
    else {
      $snippet = "(latest-problems.md not written yet)"
    }

    $openLines = @()
    foreach ($line in ($snippet -split "`r?`n")) {
      if ($line -match '^##\s+') { $openLines += $line.TrimStart('#').Trim() }
    }
    $openSummary = if ($openLines.Count -gt 0) { ($openLines -join "; ") } else { "none" }
    $context = "RELAY dogfood run $runId currently has these open problems: $openSummary. Read $latest and only the referenced event ranges before editing."
  }
  catch {
    $context = "RELAY dogfood: failed to read CURRENT/latest-problems.md ($($_.Exception.Message))."
  }
}

@{ additional_context = $context } | ConvertTo-Json -Compress
