export function normalizeDomain(input: string): string | null {
  const trimmed = input.trim().toLowerCase();
  if (!trimmed || trimmed.length > 253) return null;
  const hostport = trimmed.replace(/^[a-z][a-z0-9+.-]*:\/\//, "").split("/")[0]?.split("?")[0] ?? "";
  const host = hostport.replace(/:\d+$/, "").replace(/\.$/, "");
  if (host === "localhost") return host;
  if (!/^[a-z0-9.-]+$/.test(host) || !host.includes(".") || host.startsWith(".") || host.includes("..")) {
    return null;
  }
  return host;
}
