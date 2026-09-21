/**
 * Single source of truth for RELAY V1 verification gates.
 * Consumed by tools/verify-v1.mjs and .github/workflows/check.yml (via npm run verify:v1).
 */
export const verificationManifest = {
  version: 1,
  name: "relay-v1",
  steps: [
    { name: "format", command: "npm", args: ["run", "format:check"] },
    { name: "lint", command: "npm", args: ["run", "lint"] },
    { name: "typecheck", command: "npm", args: ["run", "typecheck"] },
    { name: "unit", command: "npm", args: ["run", "test:unit"] },
    { name: "architecture", command: "npm", args: ["run", "test:architecture"] },
    { name: "integration", command: "npm", args: ["run", "test:integration"] },
    { name: "replay", command: "npm", args: ["run", "test:replay"] },
    { name: "privacy", command: "npm", args: ["run", "test:privacy"] },
    { name: "web-export", command: "npm", args: ["run", "build:web"] },
    {
      name: "ios-export",
      command: "npm",
      args: ["run", "export:ios", "--workspace", "@relay/app"],
    },
    {
      name: "halo-install",
      command: "node",
      args: ["tools/verification/ensure-halo-test.mjs"],
    },
    { name: "halo", command: "npm", args: ["run", "halo:test"] },
    {
      name: "cargo-fmt",
      command: "cargo",
      args: ["fmt", "--check"],
      cwd: "apps/desktop/src-tauri",
    },
    {
      name: "cargo-clippy",
      command: "cargo",
      args: ["clippy", "--", "-D", "warnings"],
      cwd: "apps/desktop/src-tauri",
    },
    {
      name: "cargo-test",
      command: "cargo",
      args: ["test"],
      cwd: "apps/desktop/src-tauri",
    },
    { name: "smoke", command: "npm", args: ["run", "test:smoke"] },
    {
      name: "desktop-build",
      command: "npm",
      args: ["run", "build:desktop"],
      env: { CI: "true" },
    },
  ],
};

/** npm script names referenced by the manifest (for architecture integrity). */
export function npmScriptsReferencedByManifest() {
  const names = new Set();
  for (const step of verificationManifest.steps) {
    if (step.command === "npm" && step.args?.[0] === "run" && typeof step.args[1] === "string") {
      names.add(step.args[1]);
    }
  }
  return [...names].sort();
}
