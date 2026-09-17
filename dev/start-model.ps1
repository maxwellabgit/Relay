#Requires -Version 7.0
<#
.SYNOPSIS
  Confirm the local model endpoint is healthy (OpenAI-compatible /v1/models).
  Optionally print a suggested llama-server command; does not download weights.
#>
param(
  [string]$Endpoint = "http://127.0.0.1:8080/v1",
  [string]$Profile = (Join-Path $PSScriptRoot "profiles/local-ministral.json"),
  [switch]$StartHint
)

$ErrorActionPreference = "Stop"

if (Test-Path $Profile) {
  $cfg = Get-Content $Profile -Raw | ConvertFrom-Json
  if ($cfg.model.endpoint) { $Endpoint = $cfg.model.endpoint }
}

$modelsUrl = ($Endpoint.TrimEnd("/") + "/models")
Write-Host "Checking $modelsUrl ..."
try {
  $resp = Invoke-RestMethod -Uri $modelsUrl -Method Get -TimeoutSec 5
  Write-Host "OK: model endpoint healthy"
  $resp | ConvertTo-Json -Depth 6
  exit 0
} catch {
  Write-Warning "Model endpoint not healthy: $($_.Exception.Message)"
  if ($StartHint) {
    Write-Host @"
Suggested (example) llama.cpp server:
  llama-server -m /path/to/ministral-8b-instruct-q4.gguf --port 8080
Then re-run: ./dev/start-model.ps1
"@
  }
  exit 1
}
