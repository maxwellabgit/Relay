<#
.SYNOPSIS
  Installed NSIS smoke for RELAY desktop.

.DESCRIPTION
  Locates the built NSIS installer under target/release/bundle/nsis (or CARGO_TARGET_DIR),
  silent-installs into an isolated directory, launches relay-desktop.exe briefly,
  and records PASS/FAIL evidence. Never installs into Program Files by default.
#>
param(
  [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
  [int]$LaunchSeconds = 8
)

$ErrorActionPreference = "Stop"
Set-Location $RepoRoot

$evidenceDir = Join-Path $RepoRoot ".dev-data\nsis-smoke-latest"
New-Item -ItemType Directory -Force -Path $evidenceDir | Out-Null

$candidates = @()
if ($env:CARGO_TARGET_DIR) {
  $candidates += (Join-Path $env:CARGO_TARGET_DIR "release\bundle\nsis")
}
$candidates += (Join-Path $RepoRoot "apps\desktop\src-tauri\target\release\bundle\nsis")
$candidates += (Join-Path $RepoRoot "target\release\bundle\nsis")

$installer = $null
$nsisDir = $null
foreach ($dir in $candidates) {
  if (-not (Test-Path -LiteralPath $dir)) { continue }
  $nsisDir = $dir
  $installer = Get-ChildItem -Path $dir -Filter "*.exe" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
  if ($installer) { break }
}

$result = [ordered]@{
  startedAt = (Get-Date).ToUniversalTime().ToString("o")
  installer = $null
  installDir = $null
  launchOk = $false
  ok = $false
  detail = ""
}

if (-not $installer) {
  $searched = ($candidates -join "; ")
  $result.detail = "installer_missing searched=$searched (run npm run build:desktop first)"
  ($result | ConvertTo-Json) | Set-Content -Path (Join-Path $evidenceDir "result.json") -Encoding utf8
  Write-Host ("FAIL nsis-smoke - {0}" -f $result.detail) -ForegroundColor Red
  exit 1
}

$result.installer = $installer.FullName
$installDir = Join-Path $env:TEMP ("relay-nsis-smoke-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
$result.installDir = $installDir

Write-Host ("Installing {0} -> {1}" -f $installer.Name, $installDir)
$installArgs = @("/S", ("/D={0}" -f $installDir))
$proc = Start-Process -FilePath $installer.FullName -ArgumentList $installArgs -Wait -PassThru
if ($proc.ExitCode -ne 0) {
  $result.detail = "installer_exit_$($proc.ExitCode)"
  ($result | ConvertTo-Json) | Set-Content -Path (Join-Path $evidenceDir "result.json") -Encoding utf8
  Write-Host ("FAIL nsis-smoke - {0}" -f $result.detail) -ForegroundColor Red
  exit 1
}

$exe = Get-ChildItem -Path $installDir -Recurse -Filter "relay-desktop.exe" -ErrorAction SilentlyContinue |
  Select-Object -First 1
if (-not $exe) {
  # Some Tauri NSIS builds place the exe next to the installer unpack path or under a product folder.
  $exe = Get-ChildItem -Path $installDir -Recurse -Filter "RELAY.exe" -ErrorAction SilentlyContinue |
    Select-Object -First 1
}
if (-not $exe) {
  $result.detail = "exe_missing_after_install under $installDir"
  ($result | ConvertTo-Json) | Set-Content -Path (Join-Path $evidenceDir "result.json") -Encoding utf8
  Write-Host ("FAIL nsis-smoke - {0}" -f $result.detail) -ForegroundColor Red
  exit 1
}

$profile = Join-Path $env:TEMP ("relay-nsis-profile-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $profile | Out-Null
$env:LOCALAPPDATA = $profile

Write-Host ("Launching {0} for {1}s" -f $exe.FullName, $LaunchSeconds)
$app = Start-Process -FilePath $exe.FullName -PassThru -WindowStyle Minimized
Start-Sleep -Seconds $LaunchSeconds
if (-not $app.HasExited) {
  $result.launchOk = $true
  Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
} else {
  $result.detail = "process_exited_early_code_$($app.ExitCode)"
  ($result | ConvertTo-Json) | Set-Content -Path (Join-Path $evidenceDir "result.json") -Encoding utf8
  Write-Host ("FAIL nsis-smoke - {0}" -f $result.detail) -ForegroundColor Red
  exit 1
}

$result.ok = $true
$result.detail = "silent_install_and_launch_ok"
$result.finishedAt = (Get-Date).ToUniversalTime().ToString("o")
($result | ConvertTo-Json) | Set-Content -Path (Join-Path $evidenceDir "result.json") -Encoding utf8
Write-Host ("ok nsis-smoke - {0}" -f $result.detail)
exit 0
