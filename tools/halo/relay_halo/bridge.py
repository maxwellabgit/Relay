"""Halo display policy for RELAY.

Frames are clipped, deduplicated, and limited to glanceable kinds.
The official Brilliant transport is used only when brilliant-msg and
halo-emulator both import. Otherwise the shipping feature stays disabled.
This module does not emulate hardware.
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import sys
from dataclasses import dataclass, field
from typing import Any

ALLOWED_KINDS = frozenset({"status", "finding", "conflict", "ack"})
MAX_QUEUE = 4
MAX_BODY = 180


@dataclass
class FrameBuffer:
    width: int = 256
    height: int = 256
    last_title: str = ""
    last_body: str = ""
    cleared: bool = True
    frames: list[dict[str, Any]] = field(default_factory=list)

    def show(self, frame: dict[str, Any]) -> None:
        title = str(frame.get("title") or "")[:40]
        body = wrap_plain_text(str(frame.get("body") or ""), width=32, max_lines=6)
        self.last_title = title
        self.last_body = body
        self.cleared = False
        self.frames.append({"kind": frame.get("kind"), "title": title, "body": body})

    def clear(self) -> None:
        self.last_title = ""
        self.last_body = ""
        self.cleared = True
        self.frames.append({"kind": "clear"})

    def snapshot(self) -> dict[str, Any]:
        return {
            "width": self.width,
            "height": self.height,
            "cleared": self.cleared,
            "title": self.last_title,
            "body": self.last_body,
            "frameCount": len(self.frames),
        }


def wrap_plain_text(text: str, width: int = 32, max_lines: int = 6) -> str:
    words = text.split()
    lines: list[str] = []
    current = ""
    for word in words:
        candidate = word if not current else f"{current} {word}"
        if len(candidate) <= width:
            current = candidate
        else:
            if current:
                lines.append(current)
            current = word[:width]
        if len(lines) >= max_lines:
            break
    if current and len(lines) < max_lines:
        lines.append(current)
    result = "\n".join(lines)
    if len(words) > 0 and (len(lines) >= max_lines or len(text) > width * max_lines):
        if not result.endswith("…"):
            result = (result[: max(0, width * max_lines - 1)] + "…").strip()
    return result


def official_transport_available() -> bool:
    return importlib.util.find_spec("brilliant_msg") is not None and importlib.util.find_spec("halo_emulator") is not None


class HaloDisplay:
    """One policy adapter. Transport stays disabled until the official SDK imports."""

    def __init__(self, transport: str | None = None) -> None:
        self.fb = FrameBuffer()
        self.state = "disconnected"
        self.queue: list[tuple[str, str, str]] = []
        self.last_signature: tuple[str, str, str] | None = None
        self.acks: list[str] = []
        self.transport = transport if transport is not None else (
            "official" if official_transport_available() else "disabled"
        )

    def show(self, frame: dict[str, Any]) -> dict[str, Any]:
        if self.transport == "disabled":
            return {"ok": False, "reason": "halo_disabled", "state": self.state}
        kind = str(frame.get("kind") or "")
        if kind not in ALLOWED_KINDS:
            return {"ok": False, "reason": "kind_rejected", "state": self.state}
        body = str(frame.get("body") or "")
        if len(body) > MAX_BODY:
            return {"ok": False, "reason": "unbounded", "state": self.state}
        title = str(frame.get("title") or "")
        signature = (kind, title, body)
        if signature == self.last_signature:
            return {"ok": True, "suppressed": True, "state": self.state}
        if len(self.queue) >= MAX_QUEUE:
            return {"ok": False, "reason": "backpressure", "state": self.state}
        self.queue.append(signature)
        self.last_signature = signature
        self.fb.show({"kind": kind, "title": title, "body": body})
        return {"ok": True, "state": self.state, "snapshot": self.fb.snapshot()}

    def clear(self) -> dict[str, Any]:
        self.queue.clear()
        self.last_signature = None
        self.fb.clear()
        return {"ok": True, "state": self.state, "snapshot": self.fb.snapshot()}

    def acknowledge(self, name: str) -> dict[str, Any]:
        self.acks.append(name)
        if self.queue:
            self.queue.pop(0)
        return {"ok": True, "name": name, "state": self.state}

    def set_state(self, state: str) -> dict[str, Any]:
        allowed = {"disconnected", "scanning", "connecting", "connected", "degraded", "reconnecting"}
        if state not in allowed:
            return {"ok": False, "reason": "state_rejected", "state": self.state}
        self.state = state
        if state == "disconnected":
            self.queue.clear()
            self.fb.clear()
        return {"ok": True, "state": self.state, "snapshot": self.fb.snapshot()}


def handle_message(adapter: HaloDisplay, msg: dict[str, Any]) -> dict[str, Any]:
    version = msg.get("v", 1)
    kind = msg.get("type")
    if kind == "display.show":
        result = adapter.show(msg.get("frame") or {})
        return {"v": version, "type": "display.ack", **result}
    if kind == "display.clear":
        return {"v": version, "type": "display.ack", **adapter.clear()}
    if kind == "button":
        name = str(msg.get("name") or "unknown")
        return {"v": version, "type": "button.ack", **adapter.acknowledge(name)}
    if kind == "connection":
        return {"v": version, "type": "connection", **adapter.set_state(str(msg.get("state") or ""))}
    if kind == "snapshot":
        return {
            "v": version,
            "type": "snapshot",
            "transport": adapter.transport,
            "state": adapter.state,
            "snapshot": adapter.fb.snapshot(),
            "acks": list(adapter.acks),
            "queued": len(adapter.queue),
        }
    return {"v": version, "type": "error", "message": f"unknown_type:{kind}"}


def main() -> None:
    parser = argparse.ArgumentParser(prog="relay-halo-bridge")
    parser.add_argument("--status", action="store_true")
    args = parser.parse_args()
    adapter = HaloDisplay()
    if args.status:
        print(
            json.dumps({"v": 1, "type": "status", "transport": adapter.transport, "state": adapter.state}),
            flush=True,
        )
        return
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except json.JSONDecodeError as exc:
            print(json.dumps({"v": 1, "type": "error", "message": f"invalid_json:{exc}"}), flush=True)
            continue
        print(json.dumps(handle_message(adapter, msg)), flush=True)


if __name__ == "__main__":
    main()
