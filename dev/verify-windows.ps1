# Windows-only verification gates (§13).
# Run on a Windows host with WinUI / Desktop dependencies.
#
# Required environment:
#   TYPESAFE_API_KEY          — for live Jev (optional for fixture Desktop)
#   RELAY_LOCAL_MODEL_ENDPOINT — loopback or allowed local model endpoint
#
# Gates covered only on Windows:
#   - Relay.Desktop WinUI build
#   - tests/Relay.Tests (net10.0-windows) exclusive file lock / DPAPI paths
#   - Desktop composition against CaseRuntimeSurface / RelayCompositionFactory
#   - Hotkey + tray behaviors
#
# Usage (PowerShell):
#   .\dev\verify-windows.ps1
#   .\dev\verify-windows.ps1 -Configuration Release

param(
  [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

Write-Host "verify-windows.ps1 — Windows-only gates"
Write-Host "Configuration=$Configuration"

dotnet build src/Relay.Desktop -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Desktop build failed" }

dotnet test tests/Relay.Tests -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Relay.Tests failed" }

Write-Host "verify-windows.ps1 PASSED (Windows host)"
exit 0
