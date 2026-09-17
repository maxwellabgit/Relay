#Requires -Version 7.0
<#
.SYNOPSIS
  Bootstrap a RELAY local machine: SDK check, restore, optional worker build.
#>
param(
  [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"
Set-Location $RepoRoot

Write-Host "Repo: $RepoRoot"
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { throw "dotnet SDK not found on PATH" }
dotnet --info | Select-Object -First 20

Write-Host "Restoring solution..."
dotnet restore Relay.slnx

Write-Host "Building Core + Worker + DevHarness (Desktop requires Windows App SDK)..."
dotnet build src/Relay.Core/Relay.Core.csproj -c Debug
dotnet build src/Relay.Worker/Relay.Worker.csproj -c Debug
dotnet build src/Relay.DevHarness/Relay.DevHarness.csproj -c Debug

Write-Host "Bootstrap complete."
