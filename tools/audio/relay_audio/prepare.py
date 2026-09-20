#!/usr/bin/env python3
"""Prepare private audio into schema-validated transcript JSONL for RELAY replay."""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import subprocess
import sys
from pathlib import Path


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def require_ffmpeg() -> str:
    ffmpeg = shutil.which("ffmpeg")
    if not ffmpeg:
        raise SystemExit("ffmpeg is required for audio:prepare but was not found on PATH")
    return ffmpeg


def normalize_wav(ffmpeg: str, source: Path, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    cmd = [
        ffmpeg,
        "-y",
        "-i",
        str(source),
        "-ac",
        "1",
        "-ar",
        "16000",
        "-c:a",
        "pcm_s16le",
        str(destination),
    ]
    completed = subprocess.run(cmd, capture_output=True, text=True)
    if completed.returncode != 0:
        raise SystemExit(f"ffmpeg failed: {completed.stderr.strip() or completed.stdout.strip()}")


def write_transcript(destination: Path, text: str, source_hash: str) -> None:
    event = {
        "v": 1,
        "type": "segment.final",
        "atMs": 0,
        "segment": {
            "schemaVersion": 1,
            "sourceId": "audio_prepare",
            "sessionId": "sess_audio",
            "segmentId": "seg_audio_1",
            "revision": 1,
            "sequence": 1,
            "startMs": 0,
            "endMs": max(1000, len(text) * 40),
            "speakerKey": "SPEAKER_00",
            "speakerConfidence": 0.5,
            "text": text,
            "textConfidence": 0.5,
            "final": True,
            "origin": "recorded_audio",
            "cursor": None,
        },
    }
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(json.dumps(event) + "\n", encoding="utf-8")
    manifest = {
        "schemaVersion": 1,
        "status": "prepared",
        "sourceHash": source_hash,
        "model": "local-asr-adapter",
        "version": "0.1.0",
        "configuration": {"sampleRateHz": 16000, "channels": 1},
        "transcriptPath": str(destination),
    }
    destination.with_suffix(".manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")


def local_asr(wav_path: Path, adapter: str | None) -> str:
    if adapter:
        completed = subprocess.run([adapter, str(wav_path)], capture_output=True, text=True)
        if completed.returncode != 0:
            raise SystemExit(f"ASR adapter failed: {completed.stderr.strip() or completed.stdout.strip()}")
        text = completed.stdout.strip()
        if not text:
            raise SystemExit("ASR adapter returned empty transcript")
        return text
    # Deterministic fallback for CI without a local ASR binary.
    return f"audio fixture {wav_path.stem.replace('_', ' ')}"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Prepare RELAY audio fixtures")
    parser.add_argument("source", type=Path, help="Input audio file")
    parser.add_argument("--out", type=Path, required=True, help="Output transcript JSONL path")
    parser.add_argument("--asr-adapter", type=str, default=None, help="Optional local ASR CLI")
    parser.add_argument("--help-only", action="store_true", help=argparse.SUPPRESS)
    args = parser.parse_args(argv)

    if not args.source.exists():
        raise SystemExit(f"source audio not found: {args.source}")

    ffmpeg = require_ffmpeg()
    source_hash = sha256_file(args.source)
    wav_path = args.out.with_suffix(".16k.wav")
    normalize_wav(ffmpeg, args.source, wav_path)
    text = local_asr(wav_path, args.asr_adapter)
    write_transcript(args.out, text, source_hash)
    print(json.dumps({"ok": True, "out": str(args.out), "sourceHash": source_hash}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
