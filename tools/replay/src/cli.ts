#!/usr/bin/env node
const [, , command = "help"] = process.argv;

const messages: Record<string, string> = {
  replay: "Replay runner lands in commit 4.",
  "logs:tail": "Log tail lands in commit 6.",
  "replay:last": "Replay-last lands in commit 6.",
  "eval:thresholds": "Threshold eval lands in commit 6.",
  help: "Usage: relay-replay <replay|logs:tail|replay:last|eval:thresholds>",
};

console.log(messages[command] ?? messages.help);
process.exit(command in messages && command !== "help" ? 0 : command === "help" ? 0 : 1);
