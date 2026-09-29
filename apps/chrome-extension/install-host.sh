#!/usr/bin/env bash
set -euo pipefail
extension_id="${1:-}"
host_path="${2:-}"
if [[ ! "$extension_id" =~ ^[a-p]{32}$ ]]; then
  echo "Usage: install-host.sh <extension-id> <host-path>" >&2
  exit 1
fi
host_path="$(realpath "$host_path")"
dir="${HOME}/.config/google-chrome/NativeMessagingHosts"
mkdir -p "$dir"
manifest="$dir/app.relay.desktop.json"
cat >"$manifest" <<EOF
{
  "name": "app.relay.desktop",
  "description": "RELAY observation bridge",
  "path": "$host_path",
  "type": "stdio",
  "allowed_origins": ["chrome-extension://${extension_id}/"]
}
EOF
chmod +x "$host_path"
echo "Registered $manifest"
