# G5 recorded-audio corpus — not a green gate

Code SHA: `ea14915dc2d2029213601b2a918929ac8d2c868d`.

This slice does not green G3 or G5. No speech package is pinned. No microphone was opened. No phone was used.

## What changed

- `fixtures/public/transcripts/recorded-commitment.segments.jsonl` is a prepared `audio_file` segment list.
- `runRecordedAudio` streams that list through `RecordedAudioSource` and ingests only finals.
- Listening stays off. The snapshot segment points at an artifact. The artifact bytes are the prepared sentence.
- An interim line is streamed and is not stored as a final.

## Tests

```text
npx tsc -b --pretty false
npm run test:unit
npm run test:architecture
npm run test:integration
npm run test:replay
npm run test:privacy
```

Recorded result on this machine before the code commit: `tsc -b` exit 0, unit 147 passed, architecture 21 passed, integration 76 passed, replay 3 passed, privacy 5 passed. Toolchain: Node v22.14.0, npm 10.9.7, rustc 1.83.0, cargo 1.83.0.

These are lower-layer tests. They are not foreground speech on a device.
