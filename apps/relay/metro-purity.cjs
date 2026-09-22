/**
 * Production Metro must not keep the demo client or the acronym fixture
 * reachable. Dynamic import() still emits a chunk unless the resolver
 * swaps the module before bundling.
 */
function flagOn(env, name) {
  const value = env[name];
  return value === "1" || value === "true";
}

function redirectProductionModule(moduleName, env) {
  if (
    typeof moduleName !== "string" ||
    moduleName.includes("demo-blocked") ||
    moduleName.includes("fixture-replay-blocked")
  ) {
    return null;
  }
  const base = moduleName.split("?")[0] ?? moduleName;
  if (
    !flagOn(env, "EXPO_PUBLIC_RELAY_ALLOW_DEMO") &&
    /(?:^|\/)createBrowserDemoClient(?:\.js|\.ts)?$/.test(base)
  ) {
    return base.replace(/createBrowserDemoClient(?:\.js|\.ts)?$/, "demo-blocked");
  }
  if (
    !flagOn(env, "EXPO_PUBLIC_RELAY_DEV_CONSOLE") &&
    /(?:^|\/)replay-acronym-fixture(?:\.js|\.ts)?$/.test(base)
  ) {
    return base.replace(/replay-acronym-fixture(?:\.js|\.ts)?$/, "fixture-replay-blocked");
  }
  return null;
}

module.exports = { redirectProductionModule };
