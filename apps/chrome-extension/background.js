/* global chrome, setInterval, URL */

const HOST = "app.relay.desktop";
const DEFAULT_POLICY = {
  enabled: false,
  chromeEnabled: false,
  pageContentEnabled: false,
  permittedDomains: [],
};

let port = null;
let policy = { ...DEFAULT_POLICY, permittedDomains: [] };
let paused = false;
let policyFromDesktop = false;

chrome.sidePanel.setPanelBehavior({ openPanelOnActionClick: true }).catch(() => undefined);

chrome.storage.local.get(["policy", "paused"], (stored) => {
  if (policyFromDesktop) return;
  if (stored.policy) policy = stored.policy;
  paused = stored.paused === true;
});

function saveState() {
  chrome.storage.local.set({ policy, paused });
  chrome.runtime.sendMessage({ type: "relay.state", policy, paused, connected: Boolean(port) }).catch(() => undefined);
}

function connect() {
  if (port) return;
  try {
    port = chrome.runtime.connectNative(HOST);
  } catch {
    port = null;
    return;
  }
  port.onMessage.addListener((message) => {
    if (message && message.policy) {
      policyFromDesktop = true;
      policy = {
        enabled: message.policy.enabled === true,
        chromeEnabled: message.policy.chromeEnabled !== false,
        pageContentEnabled: message.policy.pageContentEnabled === true,
        permittedDomains: Array.isArray(message.policy.permittedDomains) ? message.policy.permittedDomains : [],
      };
      saveState();
    }
  });
  port.onDisconnect.addListener(() => {
    port = null;
    saveState();
  });
  post({ type: "chrome.hello" });
}

function post(message) {
  if (!port) return;
  try {
    port.postMessage(message);
  } catch {
    port = null;
  }
}

function domainOf(url) {
  try {
    return new URL(url).hostname.toLowerCase();
  } catch {
    return "";
  }
}

function permitted(domain) {
  return policy.permittedDomains.includes(domain);
}

function observing() {
  return !paused && policy.enabled === true && policy.chromeEnabled !== false;
}

function sendObservation(eventType, observation) {
  post({
    type: eventType,
    observation: {
      timestamp: new Date().toISOString(),
      source: { type: "chrome", provider: "extension" },
      eventType,
      application: { processName: "chrome.exe", windowTitle: observation.title || "" },
      ...observation,
    },
  });
}

async function describe(tab) {
  if (!tab || tab.active === false) return;
  const url = tab.url || "";
  if (!url.startsWith("http://") && !url.startsWith("https://")) return;
  const domain = domainOf(url);
  if (!domain || !observing() || !permitted(domain)) return;
  const base = {
    resource: { uri: url, domain, title: tab.title || "" },
    title: tab.title || "",
  };
  sendObservation(tab.status === "complete" ? "chrome.navigation" : "chrome.tab.activated", base);
  if (!policy.pageContentEnabled || tab.id == null) return;
  try {
    await chrome.scripting.executeScript({ target: { tabId: tab.id }, files: ["content.js"] });
    const extracted = await chrome.tabs.sendMessage(tab.id, { type: "relay.extract" });
    if (!extracted || extracted.error) return;
    sendObservation("chrome.page.metadata", {
      resource: { uri: url, domain, title: extracted.title || tab.title || "" },
      title: extracted.title || tab.title || "",
      data: {
        heading: extracted.heading || "",
        text: extracted.text || "",
        sections: extracted.sections || [],
        sensitiveInputsIgnored: extracted.sensitiveInputsIgnored === true,
      },
    });
  } catch {
    /* Host permission or a page without a content script target. */
  }
}

chrome.tabs.onActivated.addListener((info) => {
  chrome.tabs.get(info.tabId, (tab) => {
    if (chrome.runtime.lastError || !tab) return;
    void describe(tab);
  });
});

chrome.tabs.onUpdated.addListener((_id, change, tab) => {
  if (!tab.active) return;
  if (change.status !== "complete" && !change.title && !change.url) return;
  void describe(tab);
});

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (!message || message.type === "relay.state") return false;
  if (message.type === "relay.getState") {
    sendResponse({ policy, paused, connected: Boolean(port) });
    return false;
  }
  if (message.type === "relay.pause") {
    paused = true;
    saveState();
    sendResponse({ ok: true });
    return false;
  }
  if (message.type === "relay.resume") {
    paused = false;
    saveState();
    connect();
    sendResponse({ ok: true });
    return false;
  }
  if (message.type === "relay.permit") {
    const domain = domainOf(message.domain.includes("://") ? message.domain : `https://${message.domain}`);
    if (!domain) {
      sendResponse({ ok: false });
      return false;
    }
    chrome.permissions.request({ origins: [`https://${domain}/*`, `http://${domain}/*`] }, (granted) => {
      if (!granted) {
        sendResponse({ ok: false });
        return;
      }
      if (!policy.permittedDomains.includes(domain)) {
        policy.permittedDomains = [...policy.permittedDomains, domain];
        saveState();
      }
      sendObservation("chrome.permission.changed", {
        resource: { domain },
        data: { permitted: true },
      });
      sendResponse({ ok: true, domain });
    });
    return true;
  }
  return false;
});

connect();
setInterval(connect, 5000);
