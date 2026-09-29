# Installs the unpacked-extension native host for the current user.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "apps\desktop\src-tauri\target\release\relay-browser-bridge.exe"
if (-not (Test-Path $exe)) {
  Write-Host "Building relay-browser-bridge..."
  cargo build --release --manifest-path (Join-Path $root "apps\desktop\src-tauri\Cargo.toml") --bin relay-browser-bridge
}
if (-not (Test-Path $exe)) {
  throw "relay-browser-bridge.exe was not produced."
}
$dir = Join-Path $env:LOCALAPPDATA "RELAY\bridge"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$manifestPath = Join-Path $dir "app.relay.browser.json"
$manifest = @{
  name = "app.relay.browser"
  description = "RELAY browser bridge"
  path = $exe
  type = "stdio"
  allowed_origins = @("chrome-extension://dligodiiggdeooeemdbgpnabcloobdld/")
} | ConvertTo-Json
Set-Content -Path $manifestPath -Value $manifest -Encoding ascii
$registry = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\app.relay.browser"
New-Item -Path $registry -Force | Out-Null
Set-ItemProperty -Path $registry -Name "(default)" -Value $manifestPath
Write-Host "Native host registered."
Write-Host "Extension id: dligodiiggdeooeemdbgpnabcloobdld"
Write-Host "Load unpacked: $root\extensions\chrome"
Write-Host "Host manifest: $manifestPath"
