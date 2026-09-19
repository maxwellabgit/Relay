"""Halo NDJSON bridge for RELAY glasses display.

Reads versioned NDJSON from stdin and writes versioned NDJSON to stdout.
Uses EmulatorBrilliantMsg when --emulator is set; hardware BrilliantMsg later.
"""

from __future__ import annotations

import argparse
import json
import sys
from dataclasses import dataclass, field
from typing import Any


@dataclass
class FrameBuffer:
    width: int = 256
    height: int = 256
    last_title: str = ""
    last_body: str = ""
    cleared: bool = True
    frames: list[dict[str, Any]] = field(default_factory=list)

    def show(self, frame: dict[str, Any]) -> None:
        title = str(frame.get("title") or "")
        body = str(frame.get("body") or "")
        # TxPlainText-style truncation for 256x256 plain text.
        title = title[:40]
        body = wrap_plain_text(body, width=32, max_lines=6)
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


class EmulatorBrilliantMsg:
    """Firmware-faithful adapter shim compatible with Halo emulator surface."""

    def __init__(self) -> None:
        self.fb = FrameBuffer()
        self.connected = True
        self.buttons: list[str] = []

    def tx_plain_text(self, title: str, body: str) -> None:
        self.fb.show({"kind": "finding", "title": title, "body": body})

    def clear(self) -> None:
        self.fb.clear()

    def inject_button(self, name: str) -> None:
        self.buttons.append(name)


def handle_message(adapter: EmulatorBrilliantMsg, msg: dict[str, Any]) -> dict[str, Any]:
    v = msg.get("v", 1)
    mtype = msg.get("type")
    if mtype == "display.show":
        frame = msg.get("frame") or {}
        kind = frame.get("kind", "status")
        if kind == "clear":
            adapter.clear()
        else:
            adapter.tx_plain_text(str(frame.get("title") or ""), str(frame.get("body") or ""))
        return {"v": v, "type": "display.ack", "ok": True, "snapshot": adapter.fb.snapshot()}
    if mtype == "display.clear":
        adapter.clear()
        return {"v": v, "type": "display.ack", "ok": True, "snapshot": adapter.fb.snapshot()}
    if mtype == "button":
        name = str(msg.get("name") or "unknown")
        adapter.inject_button(name)
        return {"v": v, "type": "button.ack", "name": name}
    if mtype == "snapshot":
        return {"v": v, "type": "snapshot", "snapshot": adapter.fb.snapshot(), "buttons": list(adapter.buttons)}
    if mtype == "disconnect":
        adapter.connected = False
        return {"v": v, "type": "connection", "connected": False}
    if mtype == "reconnect":
        adapter.connected = True
        return {"v": v, "type": "connection", "connected": True}
    return {"v": v, "type": "error", "message": f"unknown_type:{mtype}"}


def main() -> None:
    parser = argparse.ArgumentParser(prog="relay-halo-bridge")
    parser.add_argument("--emulator", action="store_true", default=True)
    parser.add_argument("--interactive", action="store_true", help="pygame preview later")
    args = parser.parse_args()
    adapter = EmulatorBrilliantMsg()
    if args.interactive:
        print(json.dumps({"v": 1, "type": "info", "message": "interactive_preview_pending"}), flush=True)

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except json.JSONDecodeError as exc:
            print(json.dumps({"v": 1, "type": "error", "message": f"invalid_json:{exc}"}), flush=True)
            continue
        out = handle_message(adapter, msg)
        print(json.dumps(out), flush=True)


if __name__ == "__main__":
    main()
