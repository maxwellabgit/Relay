param(
  [Parameter(Mandatory = $true)][string]$ExtensionId,
  [Parameter(Mandatory = $true)][string]$HostPath
)

$ErrorActionPreference = "Stop"
if ($ExtensionId -notmatch '^[a-p]{32}$') {
  throw "ExtensionId must be the 32 character Chrome extension id."
}
$resolved = (Resolve-Path -LiteralPath $HostPath).Path
$dir = Join-Path $env:LOCALAPPDATA "RELAY\native-messaging"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$manifestPath = Join-Path $dir "app.relay.desktop.json"
$manifest = @{
  name = "app.relay.desktop"
  description = "RELAY observation bridge"
  path = $resolved
  type = "stdio"
  allowed_origins = @("chrome-extension://$ExtensionId/")
} | ConvertTo-Json
Set-Content -LiteralPath $manifestPath -Value $manifest -Encoding ascii
$registry = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\app.relay.desktop"
New-Item -Path $registry -Force | Out-Null
Set-ItemProperty -Path $registry -Name "(default)" -Value $manifestPath
Write-Output "Registered $manifestPath"
