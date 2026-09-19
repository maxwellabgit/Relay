# ADR-001: Cross-platform RELAY runtime

| Field | Value |
| --- | --- |
| Status | Accepted |
| Date | 2026-09-19 |
| Baseline | `a6bf987` (`relay-dotnet-a6bf987`) |
| Branch | `refactor/cross-platform-v1` |

## Decision

RELAY’s production runtime moves off WinUI and C# hosts to a shared, platform-neutral TypeScript engine with thin native adapters.

### Runtime stack

* **TypeScript engine** — one on-device Case/source/Reflex/judgment loop for Windows and iPhone.
* **Expo / React Native Web UI** — one shared component tree for the Windows workbench and the iPhone app.
* **Tauri 2** — Windows native shell (window, tray, hotkeys, SQLite, secrets, hosted-request transport, Halo sidecar).
* **On-device iPhone engine** — the same TypeScript engine runs on device; hosted Jev is reached only through a narrow native transport that protects credentials.
* **Halo emulator target** — Brilliant Labs official Halo emulator (`brilliant_sdk` / `halo_emulator`), not `frame-codebase` firmware.

### C# disposition

C# and WinUI remain **reference-only until cutover**. They characterize behavior and supply porting sources for hardened contracts. They are not a second production engine and must not receive new feature work in parallel with the TypeScript runtime.

### Adapter boundary

No platform adapter (Tauri, Expo/iOS, audio, storage, BLE, secrets) may contain Reflex decisions, thresholds, Case transitions, or feature-specific business logic. Adapters implement ports only.

## Consequences

* Stage 1 delivers a Tauri + Expo Web workbench with autonomous engine loop, replay, recorded Jev, acronym Reflex, shared SQLite schema, and live diagnostics.
* Stage 2 attaches Halo display through `GlassesDisplayPort`.
* Stage 3 ships the same contracts on iPhone via Expo/EAS/TestFlight.
* WinUI must not advance work from `RenderSurface` / pump-on-render after cutover.
