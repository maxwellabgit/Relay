#!/usr/bin/env python3
"""Live local transcript source for RELAY Windows Listen.

Emits finalized TranscriptSegmentV1 events as NDJSON on stdout.
Partial/interim transcripts are never written.

Modes:
  --fixture PATH   replay prepared JSONL/WAV-derived finals (CI / offline)
  (default)        attempt local mic capture with energy VAD; ASR optional

Never logs transcript text to stderr diagnostics.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time
from pathlib import Path


def emit_final(
    *,
    source_id: str,
    session_id: str,
    segment_id: str,
    sequence: int,
    start_ms: int,
    end_ms: int,
    text: str,
    origin: str = "microphone",
) -> None:
    event = {
        "v": 1,
        "type": "segment.final",
        "atMs": end_ms,
        "segment": {
            "schemaVersion": 1,
            "sourceId": source_id,
            "sessionId": session_id,
            "segmentId": segment_id,
            "revision": 1,
            "sequence": sequence,
            "startMs": start_ms,
            "endMs": end_ms,
            "speakerKey": None,
            "speakerConfidence": None,
            "text": text,
            "textConfidence": None,
            "final": True,
            "origin": origin,
            "cursor": None,
        },
    }
    sys.stdout.write(json.dumps(event, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def run_fixture(path: Path, session_id: str) -> int:
    sequence = 0
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line:
            continue
        row = json.loads(line)
        if row.get("type") != "segment.final":
            continue
        segment = row.get("segment") or {}
        text = str(segment.get("text") or "").strip()
        if not text:
            continue
        sequence += 1
        emit_final(
            source_id="audio_fixture",
            session_id=session_id,
            segment_id=f"seg_live_{sequence}",
            sequence=sequence,
            start_ms=int(segment.get("startMs") or 0),
            end_ms=int(segment.get("endMs") or max(1000, len(text) * 40)),
            text=text,
            origin="microphone" if segment.get("origin") == "microphone" else "audio_file",
        )
        time.sleep(0.05)
    return 0


def run_mic(session_id: str) -> int:
    """Capture 16 kHz mono with energy VAD; optional local ASR.

    Without ASR packages installed, silence detection still runs and the
    process stays healthy so RELAY can report audio status truthfully.
    Phrases are only emitted when an ASR backend is available.
    """
    try:
        import numpy as np  # type: ignore
        import sounddevice as sd  # type: ignore
    except Exception:
        sys.stderr.write("audio_backend_unavailable\n")
        sys.stderr.flush()
        while True:
            time.sleep(1.0)

    sample_rate = 16000
    frame_ms = 30
    frame = int(sample_rate * frame_ms / 1000)
    silence_frames = 18  # ~540ms
    min_voiced = 8
    sequence = 0
    buffer: list = []
    voiced = 0
    silent = 0
    started_ms = 0
    t0 = time.time()

    asr = None
    try:
        from faster_whisper import WhisperModel  # type: ignore

        asr = WhisperModel("tiny.en", device="cpu", compute_type="int8")
    except Exception:
        asr = None

    with sd.InputStream(
        samplerate=sample_rate, channels=1, dtype="float32", blocksize=frame
    ) as stream:
        while True:
            data, _overflowed = stream.read(frame)
            mono = data[:, 0]
            energy = float(np.sqrt(np.mean(np.square(mono)) + 1e-12))
            now_ms = int((time.time() - t0) * 1000)
            if energy > 0.02:
                if voiced == 0:
                    started_ms = now_ms
                voiced += 1
                silent = 0
                buffer.append(mono.copy())
            elif voiced > 0:
                silent += 1
                buffer.append(mono.copy())
                if silent >= silence_frames and voiced >= min_voiced:
                    audio = np.concatenate(buffer)
                    buffer.clear()
                    voiced = 0
                    silent = 0
                    text = ""
                    if asr is not None:
                        segments, _info = asr.transcribe(audio, language="en")
                        text = " ".join(s.text.strip() for s in segments).strip()
                    if text:
                        sequence += 1
                        emit_final(
                            source_id="tauri_audio",
                            session_id=session_id,
                            segment_id=f"seg_live_{sequence}",
                            sequence=sequence,
                            start_ms=started_ms,
                            end_ms=now_ms,
                            text=text,
                            origin="microphone",
                        )
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="RELAY live local transcript source")
    parser.add_argument("--session-id", default="session_live")
    parser.add_argument("--fixture", default=os.environ.get("RELAY_AUDIO_FIXTURE", ""))
    args = parser.parse_args()
    fixture = str(args.fixture or "").strip()
    if fixture:
        return run_fixture(Path(fixture), args.session_id)
    return run_mic(args.session_id)


if __name__ == "__main__":
    raise SystemExit(main())
