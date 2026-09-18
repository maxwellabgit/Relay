#Requires -Version 7.0
<#
.SYNOPSIS
  Build and launch Desktop for dogfooding with a fresh run root and live problem tail.
#>
param(
  [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
  [string]$DataRoot = "",
  [string]$RunId = ""
)

$ErrorActionPreference = "Stop"
Set-Location $RepoRoot

if (-not $DataRoot) {
  $DataRoot = Join-Path $RepoRoot ".dev-data"
}
if (-not $RunId) {
  $RunId = (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmss") + "-" + ([guid]::NewGuid().ToString("N").Substring(0, 8))
}

$runDir = Join-Path (Join-Path $DataRoot ".dev-runs") $RunId
New-Item -ItemType Directory -Force -Path $runDir,
  (Join-Path $runDir "payloads"),
  (Join-Path $runDir "screenshots"),
  (Join-Path $runDir "crash") | Out-Null

$gitCommit = (git -C $RepoRoot rev-parse HEAD 2>$null)
$startedAt = (Get-Date).ToUniversalTime().ToString("o")
$pointer = Join-Path (Join-Path $DataRoot ".dev-runs") "CURRENT"
[ordered]@{
  runId = $RunId
  runDir = $runDir
  dataRoot = (Resolve-Path $DataRoot).Path
  pid = $null
  commit = $gitCommit
  startedAt = $startedAt
} | ConvertTo-Json | Set-Content -Path $pointer -Encoding utf8

$env:RELAY_DATA_ROOT = (Resolve-Path $DataRoot).Path
$env:RELAY_RUN_ID = $RunId
$env:RELAY_RUN_DIR = $runDir
$env:RELAY_GIT_COMMIT = "$gitCommit"

Write-Host "Dogfood run $RunId"
Write-Host "  dataRoot: $env:RELAY_DATA_ROOT"
Write-Host "  runDir:   $runDir"
Write-Host "  latest:   $(Join-Path $runDir 'latest-problems.md')"

$desktop = Join-Path $RepoRoot "src/Relay.Desktop/Relay.Desktop.csproj"
Write-Host "Building Desktop..."
dotnet build $desktop -c Debug
if ($LASTEXITCODE -ne 0) { throw "Desktop build failed" }

$exe = Get-ChildItem -Path (Join-Path $RepoRoot "src/Relay.Desktop/bin") -Recurse -Filter "Relay.exe" |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $exe) { throw "Relay.exe not found after build" }

$proc = Start-Process -FilePath $exe.FullName -PassThru `
  -WorkingDirectory (Split-Path $exe.FullName -Parent)
$ptr = Get-Content $pointer -Raw | ConvertFrom-Json
$ptr.pid = $proc.Id
$ptr | Select-Object runId, runDir, dataRoot, pid, commit, startedAt |
  ConvertTo-Json | Set-Content -Path $pointer -Encoding utf8

Write-Host "Relay PID $($proc.Id). Tailing events/problems. Ctrl+C stops the tail (app keeps running until it exits)."
$eventsPath = Join-Path $runDir "events.jsonl"
$problemsPath = Join-Path $runDir "problems.jsonl"
$latestPath = Join-Path $runDir "latest-problems.md"
Write-Host "latest-problems.md -> $latestPath" -ForegroundColor Cyan

$seenProblems = New-Object 'System.Collections.Generic.HashSet[string]'
$eventOffset = 0L

try {
  while (-not $proc.HasExited) {
    if (Test-Path $eventsPath) {
      try {
        $fs = [System.IO.File]::Open($eventsPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        try {
          if ($eventOffset -gt $fs.Length) { $eventOffset = 0 }
          $fs.Seek($eventOffset, [System.IO.SeekOrigin]::Begin) | Out-Null
          $reader = New-Object System.IO.StreamReader($fs)
          while (($line = $reader.ReadLine()) -ne $null) {
            Write-Host $line
          }
          $eventOffset = $fs.Position
        }
        finally { $fs.Dispose() }
      }
      catch { }
    }

    if (Test-Path $problemsPath) {
      foreach ($pline in Get-Content $problemsPath -ErrorAction SilentlyContinue) {
        if ([string]::IsNullOrWhiteSpace($pline)) { continue }
        try {
          $p = $pline | ConvertFrom-Json
          $key = "$($p.problemId):$($p.occurrences)"
          if ($seenProblems.Add($key)) {
            $color = switch ($p.severity) {
              "critical" { "Red" }
              "error" { "Red" }
              "warn" { "Yellow" }
              default { "Gray" }
            }
            Write-Host ("PROBLEM [{0}] {1} — {2}" -f $p.severity, $p.signature, $p.summary) -ForegroundColor $color
          }
        }
        catch { }
      }
    }

    Start-Sleep -Milliseconds 400
  }
}
finally {
  Write-Host "App exited with code $($proc.ExitCode). latest-problems.md: $latestPath"
}
