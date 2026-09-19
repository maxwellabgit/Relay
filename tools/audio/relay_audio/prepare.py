"""Audio prepare CLI — WhisperX pipeline for recorded-audio fixtures."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser(prog="relay-audio-prepare")
    parser.add_argument("input", nargs="?", help="Path to a private recording")
    parser.add_argument("--out-dir", default="fixtures/private")
    args = parser.parse_args()
    if not args.input:
        print("audio:prepare scaffold ready; pass a private recording path.")
        print("Pipeline: hash original → 16kHz mono PCM → WhisperX ASR/align/diarize → segments.jsonl + manifest.")
        print("Never modifies the original; never commits private outputs.")
        return

    src = Path(args.input)
    if not src.exists():
        raise SystemExit(f"missing input: {src}")

    digest = sha256_file(src)
    out = Path(args.out_dir)
    out.mkdir(parents=True, exist_ok=True)
    manifest = {
        "schemaVersion": 1,
        "audioHash": digest,
        "sourcePath": str(src),
        "modelIdentifiers": {"asr": "whisperx-pending", "diarization": "whisperx-pending"},
        "configuration": {"sampleRateHz": 16000, "channels": 1},
        "generatedAt": None,
        "speakerCount": None,
        "status": "scaffold_only",
    }
    manifest_path = out / f"{src.stem}.manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(f"wrote {manifest_path} (WhisperX execution pending GPU environment)")


if __name__ == "__main__":
    main()
