/**
 * Short soak — exercises Ask under the Node harness for a bounded duration.
 * Proves the runtime stays responsive without crash under repeated work.
 */
import { mkdtemp, writeFile, mkdir } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createNodeHarness } from "../../adapters/node/src/create-client.js";

const DURATION_MS = Number(process.env.RELAY_SOAK_MS ?? "45000");

async function main(): Promise<void> {
  const evidenceDir = await mkdtemp(join(tmpdir(), "relay-soak-evidence-"));
  await mkdir(evidenceDir, { recursive: true });

  const harness = await createNodeHarness({
    databasePath: join(evidenceDir, "soak.sqlite"),
    runsRoot: join(evidenceDir, "runs"),
  });

  const started = Date.now();
  let asks = 0;
  let errors = 0;
  const deadline = started + DURATION_MS;

  try {
    while (Date.now() < deadline) {
      try {
        await harness.client.execute({ type: "SubmitText", text: `Soak ping ${asks + 1}` });
        asks += 1;
        await harness.client.getSnapshot();
      } catch (error) {
        errors += 1;
        console.error("soak_error", error);
      }
      await new Promise((r) => setTimeout(r, 250));
    }
  } finally {
    await harness.client.stop();
    harness.close();
  }

  const report = {
    durationMs: Date.now() - started,
    asks,
    errors,
    ok: errors === 0 && asks > 0,
    evidenceDir,
  };
  await writeFile(join(evidenceDir, "soak-report.json"), `${JSON.stringify(report, null, 2)}\n`);
  console.log(JSON.stringify(report));
  if (!report.ok) {
    process.exitCode = 1;
    throw new Error("soak failed");
  }
  console.log(`ok soak asks=${asks} evidence=${evidenceDir}`);
}

main().catch((error) => {
  console.error(error instanceof Error ? error.message : error);
  process.exitCode = 1;
});
