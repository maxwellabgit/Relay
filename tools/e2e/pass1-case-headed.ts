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
    await page.waitForSelector('[data-testid="relay-composer-input"]', { timeout: LAUNCH_TIMEOUT_MS });
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
    const listening = await page.locator("body").innerText();
    if (listening.includes("Listening for")) throw new Error("Listening turned on");
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
  throw new Error("Set RELAY_E2E_BINARY or build the desktop app before the headed Pass 1 journey.");
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

async function waitForText(page: Page, needles: string[], timeoutMs: number): Promise<string | null> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const body = await page.locator("body").innerText().catch(() => "");
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
