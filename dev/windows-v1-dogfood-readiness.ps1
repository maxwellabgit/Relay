<#
.SYNOPSIS
  Dogfood readiness gate for Windows V1 (no secrets printed, no downloads).

.DESCRIPTION
  Requires code readiness plus live local-model and audio/ASR prerequisites.
  Does not install packages or download Whisper models.
  Compatible with Windows PowerShell 5.1 and PowerShell 7+.
#>
param(
  [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"
Set-Location $RepoRoot

$script:failures = New-Object System.Collections.Generic.List[string]

function Add-Fail([string]$Message) {
  $script:failures.Add($Message) | Out-Null
}

Write-Host "RELAY Windows V1 dogfood readiness"
Write-Host ("repo: {0}" -f $RepoRoot)
Write-Host ""

# --- Code readiness first ---
$codeScript = Join-Path $PSScriptRoot "windows-v1-readiness.ps1"
$prev = $ErrorActionPreference
$ErrorActionPreference = "Continue"
& powershell -NoProfile -ExecutionPolicy Bypass -File $codeScript
$codeExit = $LASTEXITCODE
$ErrorActionPreference = $prev
$codeOk = ($codeExit -eq 0)
Write-Host ("Code            {0}" -f $(if ($codeOk) { "PASS" } else { "FAIL" }))
if (-not $codeOk) {
  Add-Fail "Code readiness failed"
}

# --- Local model ---
$modelPort = if ($env:RELAY_LOCAL_MODEL_PORT) { $env:RELAY_LOCAL_MODEL_PORT } else { "8080" }
$modelOk = $false
$modelDetail = "unavailable"
try {
  $resp = Invoke-WebRequest -Uri ("http://127.0.0.1:{0}/v1/models" -f $modelPort) -TimeoutSec 3 -UseBasicParsing
  if ($resp.StatusCode -ge 200 -and $resp.StatusCode -lt 300) {
    $modelOk = $true
    $modelDetail = "reachable on :{0}" -f $modelPort
    try {
      $json = $resp.Content | ConvertFrom-Json
      if ($json.data -and $json.data.Count -gt 0 -and $json.data[0].id) {
        $modelDetail = [string]$json.data[0].id
      }
    } catch {
      # keep reachable detail
    }
  }
} catch {
  $modelDetail = "not reachable on :{0}" -f $modelPort
}
Write-Host ("Local model     {0}  {1}" -f $(if ($modelOk) { "PASS" } else { "FAIL" }), $modelDetail)
if (-not $modelOk) {
  Add-Fail "Local model unavailable"
}

# --- Audio doctor (python / mic / ASR / whisper) ---
$pythonOk = $false
$micOk = $false
$asrOk = $false
$whisperOk = $false
$pythonDetail = "unavailable"
$micDetail = "unavailable"
$asrDetail = "unavailable"
$whisperDetail = "unavailable"

$audioRoot = Join-Path $RepoRoot "tools/audio"
Push-Location $audioRoot
try {
  $prevEa = $ErrorActionPreference
  $ErrorActionPreference = "Continue"
  $doctorOut = & python -m relay_audio.doctor 2>&1
  $doctorExit = $LASTEXITCODE
  $ErrorActionPreference = $prevEa

  $jsonLine = ($doctorOut | Where-Object { $_ -match '^\s*\{' } | Select-Object -Last 1)
  if ($jsonLine) {
    try {
      $status = $jsonLine | ConvertFrom-Json
      $pythonOk = [bool]$status.python.ok
      $pythonDetail = [string]$status.python.version
      $micOk = [bool]$status.mic.ok
      $micDetail = if ($status.mic.importOk) {
        if ($status.mic.sampleRate16k) { "16 kHz mic opens" } else { [string]$status.mic.detail }
      } else {
        "sounddevice import failed ($($status.mic.detail))"
      }
      $asrOk = [bool]$status.asr.importOk
      $asrDetail = if ($status.asr.importOk) { "faster-whisper import ok" } else { [string]$status.asr.detail }
      $whisperOk = [bool]$status.asr.modelAvailable
      $whisperDetail = if ($status.asr.modelAvailable) {
        [string]$status.asr.model
      } else {
        "{0} unavailable ({1})" -f $status.asr.model, $status.asr.detail
      }
      if ($doctorExit -ne 0 -and $status.ok) {
        # prefer structured fields over exit code mismatch
      }
    } catch {
      Add-Fail "relay_audio.doctor output was not valid JSON"
    }
  } else {
    Add-Fail "relay_audio.doctor did not run (install tools/audio live extras)"
    $pythonDetail = "doctor not runnable"
  }
} catch {
  Add-Fail "relay_audio.doctor failed to start"
} finally {
  Pop-Location
}

Write-Host ("Python          {0}  {1}" -f $(if ($pythonOk) { "PASS" } else { "FAIL" }), $pythonDetail)
Write-Host ("Microphone      {0}  {1}" -f $(if ($micOk) { "PASS" } else { "FAIL" }), $micDetail)
Write-Host ("ASR             {0}  {1}" -f $(if ($asrOk) { "PASS" } else { "FAIL" }), $asrDetail)
Write-Host ("Whisper model   {0}  {1}" -f $(if ($whisperOk) { "PASS" } else { "FAIL" }), $whisperDetail)
Write-Host "TypeSafe key    CHECK IN APP"

if (-not $pythonOk) { Add-Fail "Python unsupported or missing (>=3.11 required)" }
if (-not $micOk) { Add-Fail "Microphone / sounddevice 16 kHz check failed" }
if (-not $asrOk) { Add-Fail "faster-whisper import failed" }
if (-not $whisperOk) { Add-Fail "Whisper model unavailable (local_files_only)" }

Write-Host ""
if ($script:failures.Count -gt 0) {
  Write-Host "NOT READY FOR DOGFOOD" -ForegroundColor Red
  foreach ($f in $script:failures) {
    Write-Host ("- {0}" -f $f)
  }
  exit 1
}

Write-Host "READY FOR DOGFOOD"
exit 0
