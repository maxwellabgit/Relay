const { getDefaultConfig } = require("expo/metro-config");
const fs = require("fs");
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

const allowed = new Set([
  "schemaVersion",
  "sequence",
  "runId",
  "at",
  "eventType",
  "stage",
  "status",
  "sessionId",
  "episodeId",
  "caseId",
  "workId",
  "judgmentId",
  "receiptId",
  "reflexId",
  "reasonCode",
  "durationMs",
  "attempt",
  "queueDepth",
]);

function traceLine(value) {
  if (!value || typeof value !== "object") return null;
  if (Object.keys(value).some((key) => !allowed.has(key))) return null;
  if (value.schemaVersion !== 2 || typeof value.sequence !== "number") return null;
  if (typeof value.at !== "string" || typeof value.eventType !== "string") return null;
  const row = {};
  for (const key of allowed) {
    if (value[key] !== undefined) row[key] = value[key];
  }
  return row;
}

config.server.enhanceMiddleware = (middleware) => {
  return (req, res, next) => {
    const url = new URL(req.url ?? "/", "http://localhost");
    if (url.pathname !== "/__relay/trace") return middleware(req, res, next);
    const runId = url.searchParams.get("runId") ?? "";
    if (!/^run_[a-z0-9-]{1,40}$/.test(runId)) {
      res.statusCode = 400;
      res.end("rejected");
      return;
    }
    const file = path.join(workspaceRoot, ".dev-data", "runs", runId, "events.jsonl");
    if (req.method === "GET") {
      fs.readFile(file, "utf8", (error, text) => {
        const kept = [];
        if (!error) {
          for (const line of text.split("\n")) {
            if (!line.trim()) continue;
            try {
              const row = traceLine(JSON.parse(line));
              if (row) kept.push(row);
            } catch {
              // Drop a malformed line.
            }
          }
        }
        res.setHeader("Content-Type", "application/json");
        res.end(JSON.stringify(kept));
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
          const row = traceLine(JSON.parse(body));
          if (!row) {
            res.statusCode = 400;
            res.end("rejected");
            return;
          }
          fs.mkdirSync(path.dirname(file), { recursive: true });
          fs.appendFileSync(file, `${JSON.stringify(row)}\n`, "utf8");
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
