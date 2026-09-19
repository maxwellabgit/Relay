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

module.exports = config;
