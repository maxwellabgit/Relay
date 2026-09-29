/* global chrome, document, setInterval */

const status = document.querySelector("#status");
const dot = document.querySelector("#dot");
const page = document.querySelector("#page");
const domainInput = document.querySelector("#domain");
const permit = document.querySelector("#permit");
const pause = document.querySelector("#pause");

function render(state) {
  const connected = Boolean(state && state.connected);
  const paused = Boolean(state && state.paused);
  dot.classList.toggle("offline", !connected || paused);
  status.textContent = connected && !paused ? "Connected" : paused ? "Paused" : "Disconnected";
  const current = state && state.page ? state.page : "Waiting for a permitted site.";
  page.textContent = current;
  pause.textContent = paused ? "Resume observation" : "Pause observation";
}

function refresh() {
  chrome.runtime.sendMessage({ type: "relay.getState" }, (state) => {
    chrome.tabs.query({ active: true, currentWindow: true }, (tabs) => {
      const tab = tabs && tabs[0];
      const next = state || { connected: false, paused: false };
      if (tab && tab.title) next.page = tab.title;
      render(next);
    });
  });
}

permit.addEventListener("click", () => {
  chrome.runtime.sendMessage({ type: "relay.permit", domain: domainInput.value }, () => {
    domainInput.value = "";
    refresh();
  });
});

pause.addEventListener("click", () => {
  chrome.runtime.sendMessage({ type: "relay.getState" }, (state) => {
    const type = state && state.paused ? "relay.resume" : "relay.pause";
    chrome.runtime.sendMessage({ type }, refresh);
  });
});

chrome.runtime.onMessage.addListener((message) => {
  if (message && message.type === "relay.state") refresh();
});

refresh();
setInterval(refresh, 2000);
