/* global chrome, document */

function textOf(node) {
  return (node && node.innerText ? node.innerText : "").replace(/\s+/g, " ").trim();
}

function extract() {
  const password = document.querySelector(
    'input[type="password"], input[autocomplete="current-password"], input[autocomplete="new-password"]',
  );
  const card = document.querySelector('input[autocomplete="cc-number"], input[autocomplete="cc-csc"]');
  const heading = textOf(document.querySelector("h1")).slice(0, 200);
  const sections = [...document.querySelectorAll("h2")].slice(0, 8).map((node) => ({
    heading: textOf(node).slice(0, 200),
  }));
  const clone = document.body ? document.body.cloneNode(true) : null;
  if (clone) {
    clone.querySelectorAll("script,style,input,textarea,select,noscript").forEach((node) => node.remove());
  }
  const text = textOf(clone).slice(0, 4000);
  return {
    title: document.title || "",
    heading,
    sections,
    text,
    sensitiveInputsIgnored: Boolean(password || card),
  };
}

globalThis.__relayExtract = extract;
if (!globalThis.__relayListening) {
  globalThis.__relayListening = true;
  chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
    if (!message || message.type !== "relay.extract") return false;
    sendResponse(globalThis.__relayExtract());
    return false;
  });
}
