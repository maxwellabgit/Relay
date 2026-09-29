const HOST = "app.relay.browser";
const SCRIPT_ID = "relay-semantic";

let port = null;
let lastError = "Chrome is not connected.";
let connected = false;

chrome.sidePanel.setPanelBehavior({ openPanelOnActionClick: true }).catch(() => undefined);
chrome.runtime.onInstalled.addListener(() => {
  void refreshScripts();
});
chrome.runtime.onStartup.addListener(() => {
  void refreshScripts();
});

function connect() {
  if (port) return;
  try {
    port = chrome.runtime.connectNative(HOST);
  } catch (error) {
    connected = false;
    lastError = error instanceof Error ? error.message : "Native host failed to start.";
    return;
  }
  port.onMessage.addListener((message) => {
    connected = Boolean(message && message.ok);
    lastError =
      message && message.ok
        ? "Chrome is connected."
        : message?.error || "The bridge rejected a message.";
  });
  port.onDisconnect.addListener(() => {
    connected = false;
    lastError = chrome.runtime.lastError?.message || "The native host disconnected.";
    port = null;
    setTimeout(connect, 3000);
  });
  post({ v: 1, kind: "ping" });
}

function post(message) {
  connect();
  if (!port) return;
  try {
    port.postMessage(message);
  } catch (error) {
    connected = false;
    lastError = error instanceof Error ? error.message : "The bridge rejected a message.";
  }
}

setInterval(() => post({ v: 1, kind: "ping" }), 5000);
connect();

async function allowedHosts() {
  const stored = await chrome.storage.local.get({ always: [] });
  const session = await chrome.storage.session.get({ session: [] });
  return [...stored.always, ...session.session].map((host) => String(host).toLowerCase());
}

function hostOf(url) {
  try {
    return new URL(url).hostname.toLowerCase();
  } catch {
    return "";
  }
}

async function refreshScripts() {
  const hosts = await allowedHosts();
  const matches = hosts.flatMap((host) => [`https://${host}/*`, `http://${host}/*`]);
  try {
    await chrome.scripting.unregisterContentScripts({ ids: [SCRIPT_ID] });
  } catch {
    /* not registered yet */
  }
  if (matches.length === 0) return;
  try {
    await chrome.scripting.registerContentScripts([
      {
        id: SCRIPT_ID,
        js: ["content.js"],
        matches,
        runAt: "document_idle",
        persistAcrossSessions: true,
      },
    ]);
  } catch {
    /* host permission is not granted yet */
  }
}

async function forward(eventType, url, title, excerpt, permission) {
  const host = hostOf(url);
  const allowed = await allowedHosts();
  if (!host || !allowed.includes(host)) return;
  const message = {
    v: 1,
    kind: "observation",
    eventType,
    url,
    title: title || "",
    observedAt: new Date().toISOString(),
    permission: permission || "always",
  };
  if (excerpt) message.excerpt = excerpt.slice(0, 8000);
  post(message);
}

chrome.tabs.onUpdated.addListener((_id, info, tab) => {
  if (info.status !== "complete" || !tab.url) return;
  void forward("browser.navigate", tab.url, tab.title || "");
});

chrome.tabs.onActivated.addListener(async ({ tabId }) => {
  const tab = await chrome.tabs.get(tabId);
  if (!tab.url) return;
  void forward("browser.activate", tab.url, tab.title || "");
});

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message?.type === "status") {
    sendResponse({ connected, detail: lastError });
    return true;
  }
  if (message?.type === "refresh") {
    void refreshScripts()
      .then(() => sendResponse({ ok: true }))
      .catch((error) => sendResponse({ ok: false, error: String(error) }));
    return true;
  }
  if (message?.type !== "semantic") return false;
  const url = sender.tab?.url || "";
  const title = sender.tab?.title || "";
  if (sender.id !== chrome.runtime.id) return false;
  void forward(
    "page.semantic",
    url,
    title,
    typeof message.excerpt === "string" ? message.excerpt : "",
  );
  return false;
});
