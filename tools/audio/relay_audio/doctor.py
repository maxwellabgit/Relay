#!/usr/bin/env python3
"""Machine-readable local transcription readiness check for RELAY Listen.

Does not download Whisper models. Does not print secrets or transcript prose.
"""

from __future__ import annotations

import json
import os
import sys
from typing import Any

SAMPLE_RATE = 16000
DEFAULT_MODEL = "tiny.en"


def check_python() -> dict[str, Any]:
    major, minor = sys.version_info[:2]
    ok = (major, minor) >= (3, 11)
    return {
        "ok": ok,
        "version": f"{major}.{minor}.{sys.version_info.micro}",
        "requires": ">=3.11",
    }


def check_mic() -> dict[str, Any]:
    try:
        import sounddevice as sd  # type: ignore
    except Exception as exc:
        return {
            "ok": False,
            "backend": "sounddevice",
            "importOk": False,
            "sampleRate16k": False,
            "detail": type(exc).__name__,
        }

    sample_rate_ok = False
    detail = "ok"
    try:
        devices = sd.query_devices()
        default_input = sd.default.device[0] if sd.default.device is not None else None
        if default_input is None and len(devices) == 0:
            detail = "no_input_device"
        else:
            # Probe whether 16 kHz mono is acceptable without keeping the stream open.
            try:
                with sd.InputStream(
                    samplerate=SAMPLE_RATE, channels=1, dtype="float32", blocksize=512
                ):
                    sample_rate_ok = True
            except Exception as exc:
                detail = type(exc).__name__
                sample_rate_ok = False
    except Exception as exc:
        detail = type(exc).__name__

    return {
        "ok": sample_rate_ok,
        "backend": "sounddevice",
        "importOk": True,
        "sampleRate16k": sample_rate_ok,
        "detail": detail,
    }


def check_asr(model_name: str) -> dict[str, Any]:
    try:
        from faster_whisper import WhisperModel  # type: ignore
    except Exception as exc:
        return {
            "ok": False,
            "backend": "faster-whisper",
            "importOk": False,
            "model": model_name,
            "modelAvailable": False,
            "detail": type(exc).__name__,
        }

    model_available = False
    detail = "ok"
    try:
        WhisperModel(
            model_name,
            device="cpu",
            compute_type="int8",
            local_files_only=True,
        )
        model_available = True
    except Exception as exc:
        detail = type(exc).__name__
        model_available = False

    return {
        "ok": model_available,
        "backend": "faster-whisper",
        "importOk": True,
        "model": model_name,
        "modelAvailable": model_available,
        "detail": detail,
    }


def build_status() -> dict[str, Any]:
    model_name = os.environ.get("RELAY_WHISPER_MODEL", DEFAULT_MODEL).strip() or DEFAULT_MODEL
    python = check_python()
    mic = check_mic()
    asr = check_asr(model_name)
    overall = bool(python["ok"] and mic["ok"] and asr["ok"])
    return {
        "v": 1,
        "type": "doctor.status",
        "ok": overall,
        "python": python,
        "mic": mic,
        "asr": asr,
        "sampleRate": SAMPLE_RATE,
    }


def main() -> int:
    status = build_status()
    sys.stdout.write(json.dumps(status, separators=(",", ":")) + "\n")
    sys.stdout.flush()
    return 0 if status["ok"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
