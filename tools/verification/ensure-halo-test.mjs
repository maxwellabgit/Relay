#!/usr/bin/env node
/* eslint-env node */
/**
 * Ensures Halo pytest extras are installed before halo:test.
 * Fails with an actionable message when Python/pip/pytest cannot be prepared.
 */
import { spawnSync } from "node:child_process";
import { existsSync } from "node:fs";
import { resolve } from "node:path";

const root = resolve(process.cwd());
const haloDir = resolve(root, "tools/halo");
const pyproject = resolve(haloDir, "pyproject.toml");

if (!existsSync(pyproject)) {
  console.error(
    "Halo test prerequisite failed: tools/halo/pyproject.toml is missing. Restore the Halo package before running verify:v1.",
  );
  process.exit(1);
}

const pythonCandidates = process.platform === "win32" ? ["python", "py"] : ["python3", "python"];

let python = null;
for (const candidate of pythonCandidates) {
  const probe = spawnSync(candidate, ["--version"], {
    encoding: "utf8",
    shell: process.platform === "win32",
  });
  if (probe.status === 0) {
    python = candidate;
    break;
  }
}

if (!python) {
  console.error(
    "Halo test prerequisite failed: Python 3.11+ was not found on PATH. Install Python, then re-run npm run verify:v1.",
  );
  process.exit(1);
}

const install = spawnSync(python, ["-m", "pip", "install", "-e", ".[test]"], {
  cwd: haloDir,
  encoding: "utf8",
  shell: process.platform === "win32",
});

if (install.status !== 0) {
  console.error(
    "Halo test prerequisite failed: could not install tools/halo[test] (pytest). Fix pip/network, then re-run:",
  );
  console.error(`  cd tools/halo && ${python} -m pip install -e ".[test]"`);
  if (install.stderr) console.error(install.stderr.trim());
  process.exit(install.status === null ? 1 : install.status);
}

const pytestProbe = spawnSync(python, ["-m", "pytest", "--version"], {
  encoding: "utf8",
  shell: process.platform === "win32",
});

if (pytestProbe.status !== 0) {
  console.error("Halo test prerequisite failed: pytest is still unavailable after install. Run:");
  console.error(`  cd tools/halo && ${python} -m pip install -e ".[test]"`);
  process.exit(1);
}

console.log(`halo-install ok (${python}; ${pytestProbe.stdout.trim() || "pytest ready"})`);
