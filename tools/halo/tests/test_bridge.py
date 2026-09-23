import json
import subprocess
import sys
from pathlib import Path

from relay_halo.bridge import HaloDisplay, handle_message, official_transport_available, wrap_plain_text


def test_wrap_truncates_long_text():
    body = wrap_plain_text(" ".join(["word"] * 80), width=32, max_lines=6)
    assert "…" in body or len(body.splitlines()) <= 6


def test_shipping_transport_stays_disabled_without_the_official_sdk():
    adapter = HaloDisplay()
    if official_transport_available():
        assert adapter.transport == "official"
        return
    result = handle_message(
        adapter,
        {"v": 1, "type": "display.show", "frame": {"kind": "finding", "title": "API", "body": "definition"}},
    )
    assert result["ok"] is False
    assert result["reason"] == "halo_disabled"
    assert adapter.fb.snapshot()["title"] == ""


def test_policy_clips_dedupes_and_rejects_transcripts():
    adapter = HaloDisplay(transport="policy")
    rejected = handle_message(
        adapter,
        {"v": 1, "type": "display.show", "frame": {"kind": "caption", "title": "Live", "body": "raw transcript"}},
    )
    assert rejected["reason"] == "kind_rejected"
    shown = handle_message(
        adapter,
        {
            "v": 1,
            "type": "display.show",
            "frame": {"kind": "finding", "title": "API", "body": "Application Programming Interface"},
        },
    )
    assert shown["ok"] is True
    again = handle_message(
        adapter,
        {
            "v": 1,
            "type": "display.show",
            "frame": {"kind": "finding", "title": "API", "body": "Application Programming Interface"},
        },
    )
    assert again["suppressed"] is True
    snap = adapter.fb.snapshot()
    assert snap["title"] == "API"
    assert "Application Programming" in snap["body"]
    assert snap["frameCount"] == 1


def test_backpressure_clear_and_disconnect():
    adapter = HaloDisplay(transport="policy")
    for index in range(4):
        result = handle_message(
            adapter,
            {"v": 1, "type": "display.show", "frame": {"kind": "status", "title": str(index), "body": "ok"}},
        )
        assert result["ok"] is True
    blocked = handle_message(
        adapter,
        {"v": 1, "type": "display.show", "frame": {"kind": "status", "title": "5", "body": "later"}},
    )
    assert blocked["reason"] == "backpressure"
    handle_message(adapter, {"v": 1, "type": "connection", "state": "disconnected"})
    assert adapter.state == "disconnected"
    assert adapter.fb.cleared is True
    assert adapter.queue == []


def test_headless_bridge_does_not_claim_a_frame_when_disabled():
    bridge = Path(__file__).resolve().parents[1] / "relay_halo" / "bridge.py"
    proc = subprocess.run(
        [sys.executable, str(bridge)],
        input=json.dumps(
            {"v": 1, "type": "display.show", "frame": {"kind": "finding", "title": "API", "body": "definition"}}
        )
        + "\n"
        + json.dumps({"v": 1, "type": "snapshot"})
        + "\n",
        text=True,
        capture_output=True,
        check=True,
    )
    lines = [json.loads(line) for line in proc.stdout.splitlines() if line.strip()]
    assert lines[-1]["type"] == "snapshot"
    if lines[-1]["transport"] == "disabled":
        assert lines[0]["reason"] == "halo_disabled"
        assert lines[-1]["snapshot"]["title"] == ""
    else:
        assert lines[-1]["snapshot"]["title"] == "API"
