/**
 * Headed desktop journeys against the packaged Tauri binary.
 * Typing uses Playwright key events. It does not set the React value.
 *
 * Journeys that need a live local model, a Jev key, or a microphone are
 * recorded from what the packaged app actually shows. They are not marked
 * passed from a mock.
 */
import { spawn, type ChildProcess } from "node:child_process";
import { access, mkdir, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { chromium, type Browser, type Page } from "playwright-core";

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const DEBUG_PORT = Number(process.env.RELAY_E2E_CDP_PORT ?? "9334");
const LAUNCH_TIMEOUT_MS = Number(process.env.RELAY_E2E_LAUNCH_TIMEOUT_MS ?? "180000");

type JourneyStatus = "PASS" | "FAIL" | "NOT_RUN" | "HUMAN_BLOCKED" | "DEVICE_BLOCKED";

type JourneyResult = {
  id: string;
  status: JourneyStatus;
  detail: string;
};

async function main(): Promise<void> {
  if (process.platform !== "win32") {
    throw new Error("Packaged desktop journeys require Windows.");
  }
  const sha = (process.env.GIT_COMMIT ?? (await gitSha())).trim();
  const runId = `run_${Date.now().toString(36)}`;
  const evidenceDir = join(ROOT, "artifacts", "e2e", sha, runId);
  await mkdir(evidenceDir, { recursive: true });
  const profileRoot = join(tmpdir(), `relay-e2e-product-${runId}`);
  await mkdir(join(profileRoot, "RELAY"), { recursive: true });

  const binary = await resolveDesktopBinary();
  const journeys: JourneyResult[] = [];
  let child: ChildProcess | null = null;
  let browser: Browser | null = null;

  try {
    child = launchDesktop(binary, profileRoot);
    browser = await connectCdp(DEBUG_PORT, LAUNCH_TIMEOUT_MS);
    const page = await firstPage(browser);
    await page.waitForSelector('[data-testid="relay-composer-input"]', {
      timeout: LAUNCH_TIMEOUT_MS,
    });
    await waitUntilReady(page);
    await page.screenshot({ path: join(evidenceDir, "00-composer.png"), fullPage: true });

    journeys.push(
      await typedJourney(page, evidenceDir, {
        id: "05-note",
        text: "note: headed note marker",
        expect: "Saved note:",
      }),
    );
    journeys.push(
      await typedJourney(page, evidenceDir, {
        id: "05-fact",
        text: "remember that headed fact marker",
        expect: "Remembered:",
      }),
    );
    journeys.push(
      await typedJourney(page, evidenceDir, {
        id: "05-next-action",
        text: "next action: review the release ledger",
        expect: "Next:",
      }),
    );

    const chat = await typedJourney(page, evidenceDir, {
      id: "02-helpful-chat",
      text: "What is a connection-oriented transport?",
      expect: "TCP",
      alternate: "No local result for this Ask.",
    });
    if (chat.status === "PASS" && chat.detail.includes("No local result")) {
      journeys.push({
        id: "02-helpful-chat",
        status: "NOT_RUN",
        detail:
          "Packaged app has no local model server. It showed the unavailable answer instead of a model reply.",
      });
      journeys.push({
        id: "model-unavailable",
        status: "PASS",
        detail: "No local result for this Ask.",
      });
    } else {
      journeys.push(chat);
    }

    const acronym = await typedJourney(page, evidenceDir, {
      id: "07-ambiguous-acronym",
      text: "What does XYZ mean?",
      expect: "Jev",
      alternate: "No local result",
    });
    journeys.push({
      id: "07-ambiguous-acronym",
      status: acronym.detail.includes("Jev") ? "NOT_RUN" : "HUMAN_BLOCKED",
      detail: acronym.detail.includes("Jev")
        ? acronym.detail
        : "No live Jev key is configured. The packaged app did not complete a hosted choice.",
    });

    await page.getByText("Settings", { exact: true }).first().click();
    await page.getByText("No active session grant.").waitFor({ timeout: 15_000 });
    const settingsText = await page.locator("body").innerText();
    const hostedOff = settingsText.includes("Off") && settingsText.includes("not set");
    journeys.push({
      id: "10-hosted-off-no-key",
      status: hostedOff ? "PASS" : "FAIL",
      detail: hostedOff
        ? "Settings showed hosted processing Off, no key, and no session grant."
        : "Settings did not show the expected off state.",
    });
    await page.screenshot({ path: join(evidenceDir, "10-settings.png"), fullPage: true });
    await page.getByText("Close", { exact: true }).click();
    const stop = page.getByText("Stop", { exact: true });
    if ((await stop.count()) > 0) await stop.click();
    await page.getByText("Send", { exact: true }).waitFor({ timeout: 20_000 });

    await page.getByText("Listen", { exact: true }).click();
    const listen = await waitForText(page, ["Listening stays off", "Listening for"], 8_000);
    journeys.push({
      id: "06-foreground-listening",
      status: "DEVICE_BLOCKED",
      detail:
        listen === "Listening stays off"
          ? "The packaged app kept listening off and explained why. No transcript was captured."
          : listen === "Listening for"
            ? "The control showed an active listen timer. No keyword transcript was captured."
            : "Listen stayed idle. No microphone transcript was captured.",
    });
    await page.screenshot({ path: join(evidenceDir, "06-listen.png"), fullPage: true });

    await browser.close();
    browser = null;
    await stopProcess(child);
    child = null;

    child = launchDesktop(binary, profileRoot);
    browser = await connectCdp(DEBUG_PORT, LAUNCH_TIMEOUT_MS);
    const reopened = await firstPage(browser);
    await reopened.waitForSelector('[data-testid="relay-composer-input"]', {
      timeout: LAUNCH_TIMEOUT_MS,
    });
    await waitUntilReady(reopened);
    const restored = await waitForText(reopened, ["Remembered:", "Saved note:", "Next:"], 20_000);
    const statePath = join(profileRoot, "RELAY", "state.sqlite");
    await access(statePath);
    const kept = restored != null;
    journeys.push({
      id: "09-crash-recovery",
      status: kept ? "PASS" : "FAIL",
      detail: kept
        ? "Relaunch showed the saved note and state.sqlite was present."
        : "Relaunch did not show the saved note.",
    });
    await reopened.screenshot({ path: join(evidenceDir, "09-relaunch.png"), fullPage: true });
  } finally {
    if (browser) await browser.close().catch(() => undefined);
    if (child) await stopProcess(child);
  }

  const runnerCompleted = true;
  const releaseJourneysPassed = journeys.length > 0 && journeys.every((journey) => journey.status === "PASS");
  const result = {
    runnerCompleted,
    releaseJourneysPassed,
    ok: releaseJourneysPassed,
    sha,
    binary,
    journeys,
  };
  await writeFile(join(evidenceDir, "result.json"), `${JSON.stringify(result, null, 2)}\n`);
  const published = join(ROOT, ".dev-data", "e2e", "desktop-product-latest");
  await mkdir(published, { recursive: true });
  await writeFile(join(published, "result.json"), `${JSON.stringify(result, null, 2)}\n`);
  const failed = journeys.filter((journey) => journey.status !== "PASS");
  console.log(
    JSON.stringify({ evidenceDir, runnerCompleted, releaseJourneysPassed, failed: failed.length, journeys }, null, 2),
  );
  if (!releaseJourneysPassed) process.exit(1);
}

async function typedJourney(
  page: Page,
  evidenceDir: string,
  input: { id: string; text: string; expect: string; alternate?: string },
): Promise<JourneyResult> {
  const submitted = await typeAndSend(page, input.text);
  if (!submitted.ok) return { id: input.id, status: "FAIL", detail: submitted.reason };
  const found = await waitForText(
    page,
    [input.expect, input.alternate].filter((value): value is string => Boolean(value)),
    20_000,
  );
  await page.screenshot({ path: join(evidenceDir, `${input.id}.png`), fullPage: true });
  if (!found)
    return { id: input.id, status: "FAIL", detail: `timed out waiting for ${input.expect}` };
  return { id: input.id, status: "PASS", detail: found };
}

async function typeAndSend(
  page: Page,
  text: string,
): Promise<{ ok: true } | { ok: false; reason: string }> {
  const input = page.locator('[data-testid="relay-composer-input"]');
  if ((await input.count()) === 0) return { ok: false, reason: "composer input missing" };
  await input.click();
  await page.keyboard.press("Control+A");
  await page.keyboard.press("Backspace");
  await page.keyboard.type(text, { delay: 15 });
  const send = page.locator('[data-testid="relay-composer-send"]');
  if ((await send.count()) === 0) return { ok: false, reason: "composer send missing" };
  await send.click();
  return { ok: true };
}

async function waitUntilReady(page: Page): Promise<void> {
  const deadline = Date.now() + LAUNCH_TIMEOUT_MS;
  while (Date.now() < deadline) {
    const body = await page
      .locator("body")
      .innerText()
      .catch(() => "");
    if (body.length > 0 && !body.includes("still starting") && !body.includes("Starting RELAY"))
      return;
    await sleep(250);
  }
  throw new Error("RELAY did not finish starting");
}

async function waitForText(
  page: Page,
  needles: string[],
  timeoutMs: number,
): Promise<string | null> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const body = await page
      .locator("body")
      .innerText()
      .catch(() => "");
    const found = needles.find((needle) => body.includes(needle));
    if (found) return found;
    await sleep(250);
  }
  return null;
}

function launchDesktop(binary: string, profileRoot: string): ChildProcess {
  return spawn(binary, [], {
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
}

async function connectCdp(port: number, timeoutMs: number): Promise<Browser> {
  const deadline = Date.now() + timeoutMs;
  let lastError: unknown;
  while (Date.now() < deadline) {
    try {
      return await chromium.connectOverCDP(`http://127.0.0.1:${port}`);
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
    for (const context of browser.contexts()) {
      const page = context.pages()[0];
      if (page) return page;
    }
    await sleep(250);
  }
  throw new Error("WebView2 CDP connected but no page was available");
}

async function resolveDesktopBinary(): Promise<string> {
  const cargoTarget = process.env.CARGO_TARGET_DIR;
  const candidates = [
    process.env.RELAY_E2E_BINARY,
    cargoTarget ? resolve(cargoTarget, "release/relay-desktop.exe") : null,
    resolve(ROOT, "apps/desktop/src-tauri/target/release/relay-desktop.exe"),
  ].filter((value): value is string => Boolean(value));
  for (const candidate of candidates) {
    try {
      await access(candidate);
      return candidate;
    } catch {
      /* next */
    }
  }
  throw new Error("Set RELAY_E2E_BINARY or CARGO_TARGET_DIR to a built relay-desktop.exe");
}

async function gitSha(): Promise<string> {
  return new Promise((resolveSha) => {
    const child = spawn("git", ["rev-parse", "HEAD"], { cwd: ROOT });
    let out = "";
    child.stdout?.on("data", (chunk) => {
      out += String(chunk);
    });
    child.on("exit", () => resolveSha(out.trim() || "unknown"));
  });
}

async function stopProcess(child: ChildProcess): Promise<void> {
  if (child.exitCode != null) return;
  child.kill();
  const deadline = Date.now() + 8_000;
  while (child.exitCode == null && Date.now() < deadline) await sleep(100);
  if (child.exitCode == null && child.pid) {
    spawn("taskkill", ["/PID", String(child.pid), "/T", "/F"], {
      stdio: "ignore",
      windowsHide: true,
    });
    await sleep(500);
  }
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolveSleep) => setTimeout(resolveSleep, ms));
}

main().catch((error: unknown) => {
  console.error(error instanceof Error ? error.message : error);
  process.exit(1);
});
