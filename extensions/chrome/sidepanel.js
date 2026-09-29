const status = document.querySelector("#status");
const site = document.querySelector("#site");
const hostInput = document.querySelector("#host");
const allowed = document.querySelector("#allowed");

async function currentHost() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!tab?.url) return hostInput.value.trim().toLowerCase();
  try {
    return new URL(tab.url).hostname.toLowerCase();
  } catch {
    return hostInput.value.trim().toLowerCase();
  }
}

async function render() {
  const bridge = await chrome.runtime.sendMessage({ type: "status" });
  status.textContent = bridge?.detail || "Chrome is not connected.";
  status.className = bridge?.connected ? "muted" : "warn";
  const host = await currentHost();
  if (host) {
    hostInput.value = host;
    site.textContent = `This site: ${host}`;
  } else {
    site.textContent = "Type a site to allow. Chrome has not shared the address yet.";
  }
  const stored = await chrome.storage.local.get({ always: [] });
  const session = await chrome.storage.session.get({ session: [] });
  allowed.textContent = `Always: ${stored.always.join(", ") || "none"}. This session: ${session.session.join(", ") || "none"}.`;
}

async function grant(kind) {
  const host = (hostInput.value || (await currentHost())).trim().toLowerCase();
  if (!/^[a-z0-9.-]+$/.test(host)) {
    site.textContent = "Enter a site name such as www.linkedin.com.";
    return;
  }
  if (kind === "no") {
    const stored = await chrome.storage.local.get({ always: [] });
    await chrome.storage.local.set({ always: stored.always.filter((item) => item !== host) });
    const session = await chrome.storage.session.get({ session: [] });
    await chrome.storage.session.set({ session: session.session.filter((item) => item !== host) });
  } else {
    const granted = await chrome.permissions.request({ origins: [`https://${host}/*`, `http://${host}/*`] });
    if (!granted) {
      site.textContent = `Chrome did not grant access to ${host}.`;
      return;
    }
    if (kind === "always") {
      const stored = await chrome.storage.local.get({ always: [] });
      await chrome.storage.local.set({ always: [...new Set([...stored.always, host])] });
    } else {
      const session = await chrome.storage.session.get({ session: [] });
      await chrome.storage.session.set({ session: [...new Set([...session.session, host])] });
    }
  }
  await chrome.runtime.sendMessage({ type: "refresh" });
  await render();
}

document.querySelector("#always").addEventListener("click", () => void grant("always"));
document.querySelector("#session").addEventListener("click", () => void grant("session"));
document.querySelector("#no").addEventListener("click", () => void grant("no"));
document.querySelector("#open").addEventListener("click", () => {
  site.textContent = "Switch to the RELAY window. Today shows episodes after the desktop app is running.";
});
void render();
setInterval(() => void render(), 3000);
