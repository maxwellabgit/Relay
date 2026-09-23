<#
.SYNOPSIS
  Same-version install, in-place reinstall, launch, uninstall, and reinstall for RELAY NSIS.

.DESCRIPTION
  Uses an isolated directory whose path contains a space, and a private LOCALAPPDATA.
  The uninstaller must leave RELAY user data in that profile. This is not a cross-version
  upgrade and it is not a signed-installer proof.
#>
param(
  [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
  [int]$LaunchSeconds = 5
)

$ErrorActionPreference = "Stop"
Set-Location $RepoRoot

$evidenceDir = Join-Path $RepoRoot ".dev-data\nsis-lifecycle-latest"
New-Item -ItemType Directory -Force -Path $evidenceDir | Out-Null

function Write-Result([hashtable]$result, [int]$code) {
  ($result | ConvertTo-Json) | Set-Content -Path (Join-Path $evidenceDir "result.json") -Encoding utf8
  if ($code -eq 0) {
    Write-Host ("ok nsis-lifecycle - {0}" -f $result.detail)
  } else {
    Write-Host ("FAIL nsis-lifecycle - {0}" -f $result.detail) -ForegroundColor Red
  }
  exit $code
}

$candidates = @()
if ($env:CARGO_TARGET_DIR) {
  $candidates += (Join-Path $env:CARGO_TARGET_DIR "release\bundle\nsis")
}
$candidates += (Join-Path $RepoRoot "apps\desktop\src-tauri\target\release\bundle\nsis")

$installer = $null
foreach ($dir in $candidates) {
  if (-not (Test-Path -LiteralPath $dir)) { continue }
  $installer = Get-ChildItem -Path $dir -Filter "*setup.exe" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
  if ($installer) { break }
}

$result = [ordered]@{
  startedAt = (Get-Date).ToUniversalTime().ToString("o")
  installer = $null
  installDir = $null
  userData = $null
  inPlace = $false
  launchOk = $false
  uninstallOk = $false
  userDataKept = $false
  reinstallLaunchOk = $false
  pathHadSpace = $false
  ok = $false
  detail = ""
}

if (-not $installer) {
  $result.detail = "installer_missing"
  Write-Result $result 1
}

$result.installer = $installer.FullName
$installDir = Join-Path $env:TEMP ("relay lifecycle " + [guid]::NewGuid().ToString("N"))
$result.installDir = $installDir
$result.pathHadSpace = $installDir.Contains(" ")
$profile = Join-Path $env:TEMP ("relay-lifecycle-profile-" + [guid]::NewGuid().ToString("N"))
$userData = Join-Path $profile "RELAY"
New-Item -ItemType Directory -Force -Path $userData | Out-Null
$marker = Join-Path $userData "user-kept.txt"
Set-Content -Path $marker -Value "keep-me" -Encoding utf8
$result.userData = $marker

function Install-Relay {
  $proc = Start-Process -FilePath $installer.FullName -ArgumentList @("/S", ("/D={0}" -f $installDir)) -Wait -PassThru
  if ($proc.ExitCode -ne 0) {
    throw "installer_exit_$($proc.ExitCode)"
  }
}

function Find-AppExe {
  $exe = Get-ChildItem -Path $installDir -Recurse -Filter "relay-desktop.exe" -ErrorAction SilentlyContinue |
    Select-Object -First 1
  if (-not $exe) {
    $exe = Get-ChildItem -Path $installDir -Recurse -Filter "RELAY.exe" -ErrorAction SilentlyContinue |
      Select-Object -First 1
  }
  return $exe
}

function Launch-Briefly($exePath) {
  $previous = $env:LOCALAPPDATA
  $env:LOCALAPPDATA = $profile
  try {
    $app = Start-Process -FilePath $exePath -PassThru -WindowStyle Minimized
    Start-Sleep -Seconds $LaunchSeconds
    $alive = -not $app.HasExited
    if (-not $app.HasExited) {
      Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    }
    return $alive
  } finally {
    if ($null -eq $previous) { Remove-Item Env:LOCALAPPDATA -ErrorAction SilentlyContinue }
    else { $env:LOCALAPPDATA = $previous }
  }
}

try {
  Install-Relay
  Install-Relay
  $result.inPlace = $true
  $exe = Find-AppExe
  if (-not $exe) { throw "exe_missing_after_install" }
  $result.launchOk = Launch-Briefly $exe.FullName
  if (-not $result.launchOk) { throw "process_exited_early" }

  $uninstaller = Get-ChildItem -Path $installDir -Recurse -Filter "*uninstall*.exe" -ErrorAction SilentlyContinue |
    Select-Object -First 1
  if (-not $uninstaller) { throw "uninstaller_missing" }
  $removed = Start-Process -FilePath $uninstaller.FullName -ArgumentList @("/S") -Wait -PassThru
  if ($removed.ExitCode -ne 0) { throw "uninstall_exit_$($removed.ExitCode)" }
  $result.uninstallOk = $true
  $result.userDataKept = (Test-Path -LiteralPath $marker) -and ((Get-Content -LiteralPath $marker -Raw).Trim() -eq "keep-me")
  if (-not $result.userDataKept) { throw "user_data_removed" }

  Install-Relay
  $exe = Find-AppExe
  if (-not $exe) { throw "exe_missing_after_reinstall" }
  $result.reinstallLaunchOk = Launch-Briefly $exe.FullName
  if (-not $result.reinstallLaunchOk) { throw "reinstall_exited_early" }
  if (-not ((Get-Content -LiteralPath $marker -Raw).Trim() -eq "keep-me")) { throw "user_data_removed_after_reinstall" }

  $result.ok = $true
  $result.detail = "same_version_inplace_uninstall_keeps_user_data"
  $result.finishedAt = (Get-Date).ToUniversalTime().ToString("o")
  Write-Result $result 0
} catch {
  $result.detail = $_.Exception.Message
  $result.finishedAt = (Get-Date).ToUniversalTime().ToString("o")
  Write-Result $result 1
}
