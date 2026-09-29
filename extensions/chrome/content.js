function excerpt() {
  const root = document.querySelector("main, article, [role='main']") || document.body;
  if (!root) return "";
  const copy = root.cloneNode(true);
  copy
    .querySelectorAll("input, textarea, select, script, style, [type='password']")
    .forEach((node) => node.remove());
  return (copy.innerText || "").replace(/\s+\n/g, "\n").trim().slice(0, 8000);
}

chrome.runtime.sendMessage({ type: "semantic", excerpt: excerpt() });
