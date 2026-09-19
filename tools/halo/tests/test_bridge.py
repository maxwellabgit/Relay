import json
import subprocess
import sys
from pathlib import Path

import pytest

from relay_halo.bridge import EmulatorBrilliantMsg, handle_message, wrap_plain_text


def test_wrap_truncates_long_text():
    body = wrap_plain_text(" ".join(["word"] * 80), width=32, max_lines=6)
    assert "…" in body or len(body.splitlines()) <= 6


def test_listening_caption_and_finding_frames():
    adapter = EmulatorBrilliantMsg()
    handle_message(adapter, {"v": 1, "type": "display.show", "frame": {"kind": "listening", "title": "Listen", "body": "on"}})
    handle_message(adapter, {"v": 1, "type": "display.show", "frame": {"kind": "caption", "title": "Live", "body": "checking API"}})
    handle_message(
        adapter,
        {
            "v": 1,
            "type": "display.show",
            "frame": {"kind": "finding", "title": "API", "body": "Application Programming Interface"},
        },
    )
    snap = adapter.fb.snapshot()
    assert snap["title"] == "API"
    assert "Application Programming" in snap["body"]
    assert "Interface" in snap["body"]
    assert snap["frameCount"] == 3


def test_clear_and_disconnect_reconnect():
    adapter = EmulatorBrilliantMsg()
    handle_message(adapter, {"v": 1, "type": "display.show", "frame": {"kind": "status", "title": "x", "body": "y"}})
    handle_message(adapter, {"v": 1, "type": "display.clear"})
    assert adapter.fb.cleared is True
    out = handle_message(adapter, {"v": 1, "type": "disconnect"})
    assert out["connected"] is False
    out = handle_message(adapter, {"v": 1, "type": "reconnect"})
    assert out["connected"] is True


def test_button_routing():
    adapter = EmulatorBrilliantMsg()
    handle_message(adapter, {"v": 1, "type": "button", "name": "tap"})
    assert adapter.buttons == ["tap"]


def test_headless_framebuffer_snapshot_via_stdin():
    bridge = Path(__file__).resolve().parents[1] / "relay_halo" / "bridge.py"
    proc = subprocess.run(
        [sys.executable, str(bridge), "--emulator"],
        input=json.dumps({"v": 1, "type": "display.show", "frame": {"kind": "finding", "title": "API", "body": "Application Programming Interface"}})
        + "\n"
        + json.dumps({"v": 1, "type": "snapshot"})
        + "\n",
        text=True,
        capture_output=True,
        check=True,
    )
    lines = [json.loads(l) for l in proc.stdout.splitlines() if l.strip()]
    assert lines[-1]["type"] == "snapshot"
    assert lines[-1]["snapshot"]["title"] == "API"
