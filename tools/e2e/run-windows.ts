/**
 * Headed Windows E2E runner — isolated profile, real Tauri composition.
 *
 * Usage:
 *   npx tsx tools/e2e/run-windows.ts --journey msrp
 *   npx tsx tools/e2e/run-windows.ts --list
 *
 * Evidence lands under .e2e/runs/<runId>/ (gitignored via .dev-data pattern when linked).
 */
import { mkdir, writeFile } from "node:fs/promises";
import { join, resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { spawn } from "node:child_process";

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "../..");

const JOURNEYS: Readonly<
  Record<string, { readonly id: string; readonly title: string; readonly script: string }>
> = {
  msrp: {
    id: "01-deterministic-glossary",
    title: "Deterministic glossary (What is MSRP?)",
    script: "tools/e2e/msrp-desktop-headed.ts",
  },
};

async function main(): Promise<void> {
  const args = process.argv.slice(2);
  if (args.includes("--list")) {
    for (const [key, journey] of Object.entries(JOURNEYS)) {
      console.log(`${key}\t${journey.id}\t${journey.title}`);
    }
    console.log("");
    console.log("Canonical journeys 01–12: tools/e2e/golden-journeys.manifest.mjs");
    console.log("Only journey 01 is headed-product today. 02–05 and 07–12 are");
    console.log("integration-lower-layer (npm run test:e2e:golden). 06 has no harness yet.");
    return;
  }

  const journeyKey = readFlag(args, "--journey") ?? "msrp";
  const journey = JOURNEYS[journeyKey];
  if (!journey) {
    throw new Error(`Unknown journey '${journeyKey}'. Use --list.`);
  }

  const runId = `run_${Date.now()}_${journey.id}`;
  const runRoot = join(ROOT, ".e2e", "runs", runId);
  await mkdir(join(runRoot, "diagnostics"), { recursive: true });
  await mkdir(join(runRoot, "screenshots"), { recursive: true });
  await mkdir(join(runRoot, "profile"), { recursive: true });

  const startedAt = new Date().toISOString();
  const child = spawn(process.execPath, ["--import", "tsx", resolve(ROOT, journey.script)], {
    cwd: ROOT,
    env: {
      ...process.env,
      RELAY_E2E_RUN_ROOT: runRoot,
    },
    stdio: "inherit",
    shell: false,
  });

  const code: number = await new Promise((resolveExit) => {
    child.on("exit", (exitCode) => resolveExit(exitCode ?? 1));
  });

  const assertions = {
    runId,
    journey: journeyKey,
    journeyId: journey.id,
    title: journey.title,
    startedAt,
    finishedAt: new Date().toISOString(),
    exitCode: code,
    ok: code === 0,
  };
  await writeFile(
    join(runRoot, "assertions.json"),
    `${JSON.stringify(assertions, null, 2)}\n`,
    "utf8",
  );
  if (code !== 0) {
    process.exitCode = code;
    throw new Error(`Journey ${journeyKey} failed with exit ${code}`);
  }
  console.log(`ok journey=${journeyKey} run=${runId}`);
}

function readFlag(args: readonly string[], name: string): string | undefined {
  const idx = args.indexOf(name);
  if (idx < 0) return undefined;
  return args[idx + 1];
}

main().catch((error) => {
  console.error(error instanceof Error ? error.message : error);
  process.exitCode = 1;
});
