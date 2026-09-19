# Privacy

- Raw observed content is local-only by default.
- Connector content is not sent to Jev without an applicable session or Reflex disclosure grant.
- Financial content remains local-only unless the user creates a separate explicit disclosure grant.
- Derived content inherits the most restrictive source classification.
- The object store is the only place raw transcripts, message bodies, prompts, and problem-report prose may live.
- Telemetry is typed and allowlisted. It records character counts and hashes, never transcript, email, calendar, financial, prompt, or answer text.
- A unique privacy sentinel must appear only inside its encrypted object, never in SQLite metadata, JSONL diagnostics, or feed events.
- Disconnect plus delete removes imported objects and derived indexes.

Models advise. Code authorizes and executes. A confidence score never grants permission.
