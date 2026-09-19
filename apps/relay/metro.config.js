const { getDefaultConfig } = require("expo/metro-config");
const path = require("path");

const projectRoot = __dirname;
const workspaceRoot = path.resolve(projectRoot, "../..");

const config = getDefaultConfig(projectRoot);

config.watchFolders = [workspaceRoot];
config.resolver.nodeModulesPaths = [
  path.resolve(projectRoot, "node_modules"),
  path.resolve(workspaceRoot, "node_modules"),
];
config.resolver.disableHierarchicalLookup = true;

// TypeScript ESM uses `.js` specifiers that map to `.ts` sources.
const upstreamResolveRequest = config.resolver.resolveRequest;
config.resolver.resolveRequest = (context, moduleName, platform) => {
  const resolver = upstreamResolveRequest ?? context.resolveRequest;
  if (moduleName.startsWith(".") && moduleName.endsWith(".js")) {
    const withoutJs = moduleName.slice(0, -3);
    try {
      return resolver(context, withoutJs, platform);
    } catch {
      // fall through
    }
  }
  return resolver(context, moduleName, platform);
};

const fs = require("fs");
const logDir = path.join(workspaceRoot, ".dev-data", "dev-console");
const logFile = path.join(logDir, "decisions.jsonl");
const keptCodes = new Set([
  "session.started",
  "session.completed",
  "lookup.exact",
  "lookup.unknown",
  "gate.judgment_required",
  "gate.remember",
  "pattern.candidate",
  "expansion.checked",
]);

function keptLine(value) {
  if (!value || typeof value !== "object") return null;
  if (
    typeof value.sequence !== "number" ||
    typeof value.at !== "string" ||
    typeof value.code !== "string"
  ) {
    return null;
  }
  if (!keptCodes.has(value.code) || typeof value.detail !== "string") return null;
  const detail = value.detail.trim();
  if (!detail || detail.length > 160 || /[\n\r]/.test(detail)) return null;
  if (/search online|application programming interface/i.test(detail)) return null;
  if ("text" in value || "prompt" in value || "response" in value || "summary" in value)
    return null;
  return { sequence: value.sequence, at: value.at, code: value.code, detail };
}

config.server.enhanceMiddleware = (middleware) => {
  return (req, res, next) => {
    const url = (req.url ?? "").split("?")[0];
    if (url !== "/__relay/decisions") return middleware(req, res, next);
    if (req.method === "GET") {
      fs.readFile(logFile, "utf8", (error, text) => {
        const kept = [];
        if (!error) {
          for (const line of text.split("\n")) {
            const trimmed = line.trim();
            if (!trimmed) continue;
            try {
              const row = keptLine(JSON.parse(trimmed));
              if (row) kept.push(row);
            } catch {
              // Drop trash.
            }
          }
        }
        res.setHeader("Content-Type", "application/json");
        res.end(JSON.stringify(kept));
      });
      return;
    }
    if (req.method === "PUT") {
      let body = "";
      req.on("data", (chunk) => {
        body += chunk;
      });
      req.on("end", () => {
        try {
          const parsed = JSON.parse(body);
          const rows = Array.isArray(parsed) ? parsed.map(keptLine).filter(Boolean) : [];
          fs.mkdirSync(logDir, { recursive: true });
          const text =
            rows.length === 0 ? "" : `${rows.map((row) => JSON.stringify(row)).join("\n")}\n`;
          fs.writeFileSync(logFile, text, "utf8");
          res.end("ok");
        } catch {
          res.statusCode = 400;
          res.end("rejected");
        }
      });
      return;
    }
    if (req.method === "POST") {
      let body = "";
      req.on("data", (chunk) => {
        body += chunk;
      });
      req.on("end", () => {
        try {
          const row = keptLine(JSON.parse(body));
          if (!row) {
            res.statusCode = 400;
            res.end("rejected");
            return;
          }
          fs.mkdirSync(logDir, { recursive: true });
          fs.appendFileSync(logFile, `${JSON.stringify(row)}\n`, "utf8");
          res.end("ok");
        } catch {
          res.statusCode = 400;
          res.end("rejected");
        }
      });
      return;
    }
    res.statusCode = 405;
    res.end("method");
  };
};

module.exports = config;
