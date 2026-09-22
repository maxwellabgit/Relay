/**
 * Headed Windows MSRP journey through the real Tauri desktop composition.
 *
 * Requires a built desktop binary (npm run build:desktop) and a Windows display.
 * Uses an isolated LOCALAPPDATA profile; never touches the developer's real RELAY profile.
 */
import { spawn, type ChildProcess } from "node:child_process";
import { access, mkdir, mkdtemp, readdir, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { chromium, type Browser, type Page } from "playwright-core";

const ASK = "What is MSRP?";
const EXPANSION = "Manufacturer's Suggested Retail Price";
const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const DEBUG_PORT = Number(process.env.RELAY_E2E_CDP_PORT ?? "9333");
const LAUNCH_TIMEOUT_MS = Number(process.env.RELAY_E2E_LAUNCH_TIMEOUT_MS ?? "180000");
const ANSWER_TIMEOUT_MS = Number(process.env.RELAY_E2E_ANSWER_TIMEOUT_MS ?? "60000");

type Evidence = {
  profileRoot: string;
  relayRoot: string;
  screenshotAsk?: string;
  screenshotAnswer?: string;
  runDir?: string;
  eventsPath?: string;
  assertions: string[];
};

async function main(): Promise<void> {
  if (process.platform !== "win32") {
    throw new Error("Headed desktop MSRP E2E requires Windows.");
  }

  const evidenceDir = await mkdtemp(join(tmpdir(), "relay-e2e-msrp-evidence-"));
  const profileRoot = await mkdtemp(join(tmpdir(), "relay-e2e-msrp-profile-"));
  const relayRoot = join(profileRoot, "RELAY");
  await mkdir(relayRoot, { recursive: true });

  const evidence: Evidence = {
    profileRoot,
    relayRoot,
    assertions: [],
  };

  const binary = await resolveDesktopBinary();
  let child: ChildProcess | null = null;
  let browser: Browser | null = null;

  try {
    child = await launchDesktop(binary, profileRoot);
    browser = await connectCdp(DEBUG_PORT, LAUNCH_TIMEOUT_MS);
    const page = await firstPage(browser);

    await page.waitForSelector('[data-testid="relay-composer-input"]', {
      timeout: LAUNCH_TIMEOUT_MS,
    });
    evidence.assertions.push("composer_visible");

    // Allow createDesktopClient()/start() to attach before driving Ask.
    await page.waitForTimeout(2000);
    evidence.assertions.push("desktop_client_settle");

    evidence.screenshotAsk = join(evidenceDir, "01-composer-ready.png");
    await page.screenshot({ path: evidence.screenshotAsk, fullPage: true });

    const submitted = await submitAskThroughComposer(page, ASK);
    if (!submitted.ok) {
      throw new Error(`composer submit failed: ${submitted.reason}`);
    }
    evidence.assertions.push(`composer_submit_invoked:${submitted.path}`);

    try {
      await page.waitForFunction(
        (expected) => {
          const body = document.body?.innerText ?? "";
          return body.includes(expected.ask) && body.includes(expected.expansion);
        },
        { ask: ASK, expansion: EXPANSION },
        { timeout: ANSWER_TIMEOUT_MS },
      );
    } catch (error) {
      const failureShot = join(evidenceDir, "failure.png");
      await page.screenshot({ path: failureShot, fullPage: true });
      const bodyText = (await page.locator("body").innerText()).slice(0, 4000);
      await writeFile(join(evidenceDir, "failure-body.txt"), bodyText, "utf8");
      throw new Error(
        `Ask/answer not rendered. screenshot=${failureShot} bodyPreview=${JSON.stringify(bodyText.slice(0, 500))} cause=${String(error)}`,
      );
    }
    evidence.assertions.push("ask_and_answer_rendered");
    evidence.screenshotAnswer = join(evidenceDir, "02-ask-answer.png");
    await page.screenshot({ path: evidence.screenshotAnswer, fullPage: true });

    const timeline = await page.textContent("body");
    if (timeline?.includes("Jev Decision Tree")) {
      throw new Error("UI still labels the surface as Jev Decision Tree");
    }
    if (!timeline?.includes("Execution Timeline")) {
      // Developer console may be off-screen on narrow layouts; do not fail solely on this,
      // but record it when present.
      evidence.assertions.push("execution_timeline_label_not_observed_in_body");
    } else {
      evidence.assertions.push("execution_timeline_label_visible");
    }

    const run = await waitForRunArtifacts(relayRoot, ANSWER_TIMEOUT_MS);
    evidence.runDir = run.runDir;
    evidence.eventsPath = run.eventsPath;
    await assertStructuralLogs(run.eventsPath);
    evidence.assertions.push("structural_logs_ok_zero_jev_zero_model");

    await browser.close();
    browser = null;
    await stopProcess(child);
    child = null;

    // Restart persistence: relaunch same isolated profile and confirm SQLite survived.
    child = await launchDesktop(binary, profileRoot);
    browser = await connectCdp(DEBUG_PORT, LAUNCH_TIMEOUT_MS);
    const page2 = await firstPage(browser);
    await page2.waitForSelector('[data-testid="relay-composer-input"]', {
      timeout: LAUNCH_TIMEOUT_MS,
    });
    const statePath = join(relayRoot, "state.sqlite");
    await access(statePath);
    evidence.assertions.push("restart_state_sqlite_present");

    await browser.close();
    browser = null;
    await stopProcess(child);
    child = null;

    const bundlePath = join(evidenceDir, "result.json");
    await writeFile(
      bundlePath,
      `${JSON.stringify({ ok: true, ask: ASK, expansion: EXPANSION, evidence }, null, 2)}\n`,
      "utf8",
    );

    // Publish under repo .dev-data for e2e:last later, without touching real profile.
    const published = resolve(ROOT, ".dev-data", "e2e", "msrp-headed-latest");
    await rm(published, { recursive: true, force: true });
    await mkdir(published, { recursive: true });
    await writeFile(join(published, "result.json"), await readFile(bundlePath));
    if (evidence.screenshotAsk) {
      await writeFile(
        join(published, "01-composer-filled.png"),
        await readFile(evidence.screenshotAsk),
      );
    }
    if (evidence.screenshotAnswer) {
      await writeFile(
        join(published, "02-ask-answer.png"),
        await readFile(evidence.screenshotAnswer),
      );
    }
    if (evidence.eventsPath) {
      await writeFile(join(published, "events.jsonl"), await readFile(evidence.eventsPath));
    }

    console.log("MSRP headed desktop E2E PASS");
    console.log(`evidence: ${published}`);
  } finally {
    if (browser) {
      try {
        await browser.close();
      } catch {
        /* ignore */
      }
    }
    if (child) {
      await stopProcess(child);
    }
    // Cleanup only the isolated profile — never the developer's real LOCALAPPDATA\RELAY.
    await sleep(1500);
    try {
      await rm(profileRoot, { recursive: true, force: true });
      evidence.assertions.push("isolated_profile_cleaned");
    } catch (error) {
      evidence.assertions.push(`isolated_profile_cleanup_deferred:${String(error)}`);
    }
  }
}

async function resolveDesktopBinary(): Promise<string> {
  const cargoTarget = process.env.CARGO_TARGET_DIR;
  const candidates = [
    process.env.RELAY_E2E_BINARY,
    cargoTarget ? resolve(cargoTarget, "release/relay-desktop.exe") : null,
    cargoTarget ? resolve(cargoTarget, "release/RELAY.exe") : null,
    cargoTarget ? resolve(cargoTarget, "debug/relay-desktop.exe") : null,
    resolve(ROOT, "apps/desktop/src-tauri/target/release/RELAY.exe"),
    resolve(ROOT, "apps/desktop/src-tauri/target/release/relay-desktop.exe"),
    resolve(ROOT, "apps/desktop/src-tauri/target/debug/RELAY.exe"),
    resolve(ROOT, "apps/desktop/src-tauri/target/debug/relay-desktop.exe"),
  ].filter((value): value is string => Boolean(value));

  for (const candidate of candidates) {
    try {
      await access(candidate);
      return candidate;
    } catch {
      /* try next */
    }
  }

  throw new Error(
    "Headed MSRP E2E requires a built Tauri binary. Run `npm run build:desktop` first, or set RELAY_E2E_BINARY.",
  );
}

async function launchDesktop(binary: string, profileRoot: string): Promise<ChildProcess> {
  const child = spawn(binary, [], {
    cwd: ROOT,
    env: {
      ...process.env,
      LOCALAPPDATA: profileRoot,
      WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${DEBUG_PORT}`,
      RELAY_E2E: "1",
    },
    stdio: ["ignore", "pipe", "pipe"],
    windowsHide: false,
  });

  child.stdout?.on("data", (chunk) => {
    if (process.env.RELAY_E2E_VERBOSE === "1") process.stdout.write(chunk);
  });
  child.stderr?.on("data", (chunk) => {
    if (process.env.RELAY_E2E_VERBOSE === "1") process.stderr.write(chunk);
  });

  child.on("exit", (code, signal) => {
    if (process.env.RELAY_E2E_VERBOSE === "1") {
      console.error(`desktop exited code=${code} signal=${signal}`);
    }
  });

  return child;
}

async function connectCdp(port: number, timeoutMs: number): Promise<Browser> {
  const deadline = Date.now() + timeoutMs;
  let lastError: unknown;
  while (Date.now() < deadline) {
    try {
      const browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`);
      return browser;
    } catch (error) {
      lastError = error;
      await sleep(500);
    }
  }
  throw new Error(`Failed to connect to WebView2 CDP on port ${port}: ${String(lastError)}`);
}

async function firstPage(browser: Browser): Promise<Page> {
  const deadline = Date.now() + LAUNCH_TIMEOUT_MS;
  while (Date.now() < deadline) {
    const contexts = browser.contexts();
    for (const context of contexts) {
      const pages = context.pages();
      if (pages[0]) return pages[0];
    }
    await sleep(250);
  }
  throw new Error("WebView2 CDP connected but no page was available");
}

async function waitForRunArtifacts(
  relayRoot: string,
  timeoutMs: number,
): Promise<{ runDir: string; eventsPath: string }> {
  const runsRoot = join(relayRoot, "diagnostics", "runs");
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const dirs = (await readdir(runsRoot)).filter((name) => name.startsWith("run_"));
      dirs.sort();
      const latest = dirs[dirs.length - 1];
      if (latest) {
        const runDir = join(runsRoot, latest);
        const eventsPath = join(runDir, "events.jsonl");
        const text = await readFile(eventsPath, "utf8");
        if (text.includes("answer.committed")) {
          return { runDir, eventsPath };
        }
      }
    } catch {
      /* not ready */
    }
    await sleep(250);
  }
  throw new Error(`Timed out waiting for run artifacts under ${runsRoot}`);
}

async function assertStructuralLogs(eventsPath: string): Promise<void> {
  const lines = (await readFile(eventsPath, "utf8"))
    .split("\n")
    .map((line) => line.trim())
    .filter(Boolean)
    .map((line) => JSON.parse(line) as { eventType?: string; reasonCode?: string });

  const required = [
    "source.accepted",
    "case.created",
    "reflex.detected",
    "policy.evaluated",
    "answer.committed",
  ] as const;

  let cursor = 0;
  for (const eventType of required) {
    const found = lines.slice(cursor).findIndex((line) => line.eventType === eventType);
    if (found < 0) {
      throw new Error(`missing structural event ${eventType} in ${eventsPath}`);
    }
    cursor += found + 1;
  }

  const forbidden = lines.filter(
    (line) => line.eventType?.startsWith("model.") || line.eventType?.startsWith("judgment."),
  );
  if (forbidden.length > 0) {
    throw new Error(
      `unexpected provider events: ${forbidden.map((line) => line.eventType).join(",")}`,
    );
  }

  const raw = await readFile(eventsPath, "utf8");
  if (raw.includes(ASK) || raw.toLowerCase().includes("manufacturer")) {
    throw new Error("structural logs leaked Ask text or expansion prose");
  }
}

async function stopProcess(child: ChildProcess): Promise<void> {
  if (child.exitCode != null) return;
  child.kill();
  const deadline = Date.now() + 10_000;
  while (child.exitCode == null && Date.now() < deadline) {
    await sleep(100);
  }
  if (child.exitCode == null && child.pid) {
    spawn("taskkill", ["/PID", String(child.pid), "/T", "/F"], {
      stdio: "ignore",
      windowsHide: true,
    });
    await sleep(1000);
  }
}

/**
 * Type into the composer and press Send.
 * Uses the DOM value setter plus an input event so the controlled field updates,
 * then clicks the Send control. Does not walk React internals.
 */
async function submitAskThroughComposer(
  page: Page,
  text: string,
): Promise<{ ok: true; path: string } | { ok: false; reason: string }> {
  const input = page.locator('[data-testid="relay-composer-input"]');
  if ((await input.count()) === 0) return { ok: false, reason: "composer input missing" };
  const typed = await page.evaluate((value) => {
    const root = document.querySelector('[data-testid="relay-composer-input"]');
    const node =
      root instanceof HTMLInputElement || root instanceof HTMLTextAreaElement
        ? root
        : (root?.querySelector("textarea, input") ?? null);
    if (!(node instanceof HTMLInputElement || node instanceof HTMLTextAreaElement)) {
      return { ok: false as const, reason: "composer input missing" };
    }
    const prototype = Object.getPrototypeOf(node) as {
      value?: { set?: (v: string) => void };
    } | null;
    const setter = prototype ? Object.getOwnPropertyDescriptor(prototype, "value")?.set : undefined;
    if (!setter) return { ok: false as const, reason: "composer value setter missing" };
    setter.call(node, value);
    node.dispatchEvent(
      new InputEvent("input", { bubbles: true, data: value, inputType: "insertText" }),
    );
    return { ok: true as const };
  }, text);
  if (!typed.ok) return typed;
  const send = page.locator('[data-testid="relay-composer-send"]');
  if ((await send.count()) === 0) return { ok: false, reason: "composer send missing" };
  await send.click();
  return { ok: true, path: "composer_input_event" };
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolveSleep) => setTimeout(resolveSleep, ms));
}

main().catch((error) => {
  console.error("MSRP headed desktop E2E FAIL");
  console.error(error instanceof Error ? (error.stack ?? error.message) : error);
  process.exit(1);
});
