# Privacy

- Raw observed content is local-only by default. Sensitivity flags (personal, email, calendar, source code, financial) are orthogonal to disclosure class.
- Connector content is not sent to Jev without an applicable session or Reflex disclosure grant.
- Financial content remains local-only unless the user creates a separate explicit disclosure grant.
- Derived content inherits the strictest disclosure class and unions every sensitivity flag.
- The object store is the only place raw transcripts, message bodies, prompts, and problem-report prose may live.
- Telemetry is typed and allowlisted per event name. Unknown property keys are rejected. It records counts and opaque object refs, never transcript, email, calendar, financial, prompt, answer, expected/actual prose, or content hashes.
- A unique privacy sentinel must appear only inside its encrypted object, never in SQLite metadata, JSONL diagnostics, or feed events.
- Disconnect and imported-content deletion are separate confirmations with separate receipts.

Models advise. Code authorizes and executes. A confidence score never grants permission.
