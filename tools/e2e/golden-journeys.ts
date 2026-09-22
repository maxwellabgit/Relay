/**
 * Golden journeys 2–12 — Node integration proofs for Phase 8 release.
 * Journey 1 (MSRP headed desktop) is run separately via test:e2e:msrp / run-windows.
 */
import { spawn } from "node:child_process";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "../..");

const SUITES: readonly { readonly id: string; readonly title: string; readonly file: string }[] = [
  {
    id: "02-helpful-chat",
    title: "Helpful chat / tool kernel Ask paths",
    file: "adapters/node/src/tool-kernel.integration.test.ts",
  },
  {
    id: "03-tool-assisted-ask",
    title: "Tool-assisted Ask with public-search",
    file: "adapters/node/src/tool-kernel.integration.test.ts",
  },
  {
    id: "04-05-ambient",
    title: "Ambient ignore + note recommendation",
    file: "adapters/node/src/ambient-triage.integration.test.ts",
  },
  {
    id: "07-ambiguous-acronym",
    title: "Ambiguous acronym + bounded Jev",
    file: "adapters/node/src/bounded-expansion.integration.test.ts",
  },
  {
    id: "08-claim-verification",
    title: "Claim verification",
    file: "adapters/node/src/claim-verify.integration.test.ts",
  },
  {
    id: "10-write-authority",
    title: "Write authority / operations",
    file: "adapters/node/src/tool-kernel.integration.test.ts",
  },
  {
    id: "11-12-pattern-lifecycle",
    title: "Pattern proposal + shadow activation",
    file: "adapters/node/src/bounded-expansion.integration.test.ts",
  },
  {
    id: "09-crash-recovery",
    title: "Crash recovery / restart isolation",
    file: "adapters/node/src/windows-v1-crash-recovery.integration.test.ts",
  },
];

async function main(): Promise<void> {
  const files = [...new Set(SUITES.map((s) => s.file))];
  console.log("Phase 8 golden journeys (integration proofs)");
  for (const suite of SUITES) {
    console.log(`- ${suite.id}: ${suite.title}`);
  }
  console.log("");
  console.log(`Running vitest on ${files.length} suites…`);

  const code: number = await new Promise((resolveExit) => {
    const child = spawn(
      process.platform === "win32" ? "npx.cmd" : "npx",
      ["vitest", "run", "--project", "integration", ...files],
      { cwd: ROOT, stdio: "inherit", shell: true },
    );
    child.on("exit", (exitCode) => resolveExit(exitCode ?? 1));
  });

  if (code !== 0) {
    process.exitCode = code;
    throw new Error(`golden journeys failed with exit ${code}`);
  }
  console.log("ok golden journeys (integration)");
}

main().catch((error) => {
  console.error(error instanceof Error ? error.message : error);
  process.exitCode = 1;
});
