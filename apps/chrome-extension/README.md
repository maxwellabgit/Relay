# RELAY Chrome observation

This is the Manifest V3 extension that reports permitted Chrome activity to the RELAY desktop app. It does not log keystrokes, passwords, or unapproved page contents.

## Load the extension

1. Open `chrome://extensions`.
2. Turn on Developer mode.
3. Choose **Load unpacked** and select this `apps/chrome-extension` directory.
4. Copy the extension ID.

## Build the native host

From the repository root, on Windows:

```powershell
cargo build --release --manifest-path apps/native-host/Cargo.toml
```

The executable is `apps/native-host/target/release/relay-native-host.exe`.

## Register the host

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File apps/chrome-extension/install-host.ps1 -ExtensionId <extension-id> -HostPath <absolute-path-to-relay-native-host.exe>
```

On Linux or macOS, for a local Chrome profile:

```bash
bash apps/chrome-extension/install-host.sh <extension-id> <absolute-path-to-relay-native-host>
```

Restart Chrome after registration.

## Use it

1. Launch RELAY Desktop.
2. Open **Today** and turn **Observation** and **Chrome** on. Page content stays off until you enable it.
3. Open the RELAY side panel, enter a site such as `jobs.example.com`, and choose **Permit site**.
4. Browse that site. RELAY correlates the Chrome page with the Windows Chrome window.

The side panel shows whether the native host is connected. The host only forwards observation messages. It cannot run desktop tools.

Bridge state is written to `%LOCALAPPDATA%\RELAY\chrome-bridge.json` while Chrome observation is on. The file holds a loopback port and a random token. It is removed when observation stops.
