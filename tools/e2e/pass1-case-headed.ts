/**
 * Headed Pass 1 check: isolated profile, real Tauri window, Cases surface.
 * Listening stays off. This does not claim a live calendar connector.
 */
import { spawn, type ChildProcess } from "node:child_process";
import { access, mkdir, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { chromium, type Browser, type Page } from "playwright-core";

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const DEBUG_PORT = Number(process.env.RELAY_E2E_CDP_PORT ?? "9335");
const LAUNCH_TIMEOUT_MS = Number(process.env.RELAY_E2E_LAUNCH_TIMEOUT_MS ?? "180000");

async function main(): Promise<void> {
  if (process.platform !== "win32") throw new Error("Pass 1 headed journey requires Windows.");
  const sha = (process.env.GIT_COMMIT ?? (await gitSha())).trim();
  const runId = `run_${Date.now().toString(36)}`;
  const evidenceDir = join(ROOT, "artifacts", "e2e", sha, `pass1-${runId}`);
  await mkdir(evidenceDir, { recursive: true });
  const profileRoot = join(tmpdir(), `relay-e2e-pass1-${runId}`);
  await mkdir(join(profileRoot, "RELAY"), { recursive: true });
  const binary = await resolveDesktopBinary();
  let child: ChildProcess | null = null;
  let browser: Browser | null = null;
  const assertions: string[] = [];
  try {
    child = spawn(binary, [], {
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
    browser = await connectCdp(DEBUG_PORT);
    const page = await firstPage(browser);
    await page.waitForSelector('[data-testid="relay-composer-input"]', {
      timeout: LAUNCH_TIMEOUT_MS,
    });
    assertions.push("composer_visible");
    await page.screenshot({ path: join(evidenceDir, "00-ready.png"), fullPage: true });
    const cases = page.locator('[data-testid="relay-nav-cases"]');
    if ((await cases.count()) === 0) throw new Error("Cases navigation missing");
    await cases.click();
    const found = await waitForText(page, ["Birthdays"], 15_000);
    const body = await page.locator("body").innerText();
    await page.screenshot({ path: join(evidenceDir, "01-cases.png"), fullPage: true });
    await writeFile(join(evidenceDir, "01-cases.txt"), body);
    if (!found) throw new Error(`Birthdays Case was not visible: ${body.slice(0, 500)}`);
    assertions.push("birthdays_visible");
    const sample = page.locator('[data-testid="relay-e2e-calendar"]');
    if ((await sample.count()) === 0) throw new Error("Calendar sample control missing");
    await sample.click();
    const noted = await waitForText(page, ["Birthday noted", "Maya"], 20_000);
    if (!noted) throw new Error("Birthday notice did not appear");
    assertions.push("birthday_noted");
    await page.screenshot({ path: join(evidenceDir, "02-birthday.png"), fullPage: true });
    await page.locator('[data-testid="relay-nav-verify"]').click();
    const conflict = await waitForText(page, ["Contradicted", "04-01"], 15_000);
    if (!conflict) throw new Error("Verify conflict was not visible");
    assertions.push("verify_conflict");
    await page.screenshot({ path: join(evidenceDir, "03-verify.png"), fullPage: true });
    await page.locator('[data-testid="relay-verify-accept"]').click();
    await page.locator('[data-testid="relay-nav-cases"]').click();
    const accepted = await waitForText(page, ["Maya 04-01"], 20_000);
    if (!accepted) {
      const after = await page.locator("body").innerText();
      await writeFile(join(evidenceDir, "04-after-accept.txt"), after);
      await page.screenshot({ path: join(evidenceDir, "04-after-accept.png"), fullPage: true });
      throw new Error(`Accepted birthday was not visible: ${after.slice(0, 700)}`);
    }
    assertions.push("verify_accepted");
    await page.screenshot({ path: join(evidenceDir, "04-accepted.png"), fullPage: true });
    await browser.close();
    browser = null;
    if (child.exitCode == null) child.kill();
    await waitForExit(child, 15_000);
    child = spawn(binary, [], {
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
    browser = await connectCdp(DEBUG_PORT);
    const reopened = await firstPage(browser);
    await reopened.waitForSelector('[data-testid="relay-composer-input"]', {
      timeout: LAUNCH_TIMEOUT_MS,
    });
    await reopened.locator('[data-testid="relay-nav-cases"]').click();
    const persisted = await waitForText(reopened, ["Maya 04-01"], 20_000);
    const reopenedBody = await reopened.locator("body").innerText();
    await reopened.screenshot({ path: join(evidenceDir, "05-restart.png"), fullPage: true });
    await writeFile(join(evidenceDir, "05-restart.txt"), reopenedBody);
    if (!persisted)
      throw new Error(`Accepted birthday did not survive restart: ${reopenedBody.slice(0, 500)}`);
    assertions.push("restart_keeps_accepted");
    if (reopenedBody.includes("Listening for")) throw new Error("Listening turned on");
    assertions.push("listening_off");
    await writeFile(
      join(evidenceDir, "result.json"),
      `${JSON.stringify({ sha, binary, profileRoot, assertions, status: "PASS" }, null, 2)}\n`,
    );
    console.log(JSON.stringify({ evidenceDir, status: "PASS", assertions }));
  } catch (error) {
    await writeFile(
      join(evidenceDir, "result.json"),
      `${JSON.stringify({ sha, assertions, status: "FAIL", error: String(error) }, null, 2)}\n`,
    );
    throw error;
  } finally {
    await browser?.close().catch(() => undefined);
    if (child && child.exitCode == null) child.kill();
  }
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
  throw new Error(
    "Set RELAY_E2E_BINARY or build the desktop app before the headed Pass 1 journey.",
  );
}

async function connectCdp(port: number): Promise<Browser> {
  const deadline = Date.now() + LAUNCH_TIMEOUT_MS;
  let lastError: unknown;
  while (Date.now() < deadline) {
    try {
      return await chromium.connectOverCDP(`http://127.0.0.1:${port}`);
    } catch (error) {
      lastError = error;
      await sleep(500);
    }
  }
  throw new Error(`CDP connect failed: ${String(lastError)}`);
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
  throw new Error("No WebView page");
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

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function waitForExit(child: ChildProcess, timeoutMs: number): Promise<void> {
  if (child.exitCode != null) return Promise.resolve();
  return new Promise((resolve) => {
    const timer = setTimeout(resolve, timeoutMs);
    child.once("exit", () => {
      clearTimeout(timer);
      resolve();
    });
  });
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

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
