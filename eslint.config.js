import eslint from "@eslint/js";
import tseslint from "typescript-eslint";
import importPlugin from "eslint-plugin-import";

const platformForbidden = [
  "fs",
  "path",
  "react",
  "react-native",
  "expo",
  "better-sqlite3",
  "WebBluetooth",
  "brilliant-ble",
];

export default tseslint.config(
  {
    ignores: [
      "**/dist/**",
      "**/dist-types/**",
      "**/node_modules/**",
      "**/.expo/**",
      "**/src-tauri/target/**",
      "src/**",
      "tests/**",
      "artifacts/**",
      ".dev-data/**",
      ".dev-runs/**",
      "Projects/**",
      "dev/**",
      "tools/verify-v1.mjs",
      "tools/with-git-sha.mjs",
      "tools/with-demo-flag.mjs",
      "tools/verification/**/*.mjs",
      "tools/e2e/**/*.mjs",
      "apps/relay/babel.config.js",
      "apps/relay/metro.config.js",
      "apps/relay/metro-purity.cjs",
      "apps/relay/index.js",
    ],
  },
  eslint.configs.recommended,
  ...tseslint.configs.recommended,
  {
    plugins: {
      import: importPlugin,
    },
    settings: {
      "import/resolver": {
        typescript: {
          project: true,
        },
      },
    },
    rules: {
      "import/no-extraneous-dependencies": "off",
      "@typescript-eslint/consistent-type-imports": [
        "error",
        { prefer: "type-imports", fixStyle: "inline-type-imports" },
      ],
    },
  },
  {
    files: ["packages/engine/src/**/*.{ts,tsx}", "packages/contracts/src/**/*.{ts,tsx}"],
    ignores: ["**/*.test.ts", "**/*.architecture.test.ts"],
    rules: {
      "no-restricted-imports": [
        "error",
        {
          paths: platformForbidden.map((name) => ({
            name,
            message:
              "Engine and contracts must stay platform-neutral. Put platform I/O in adapters.",
          })),
          patterns: [
            {
              group: [
                "node:*",
                "expo",
                "expo-*",
                "@tauri-apps/*",
                "react",
                "react-native",
                "react-native-*",
              ],
              message:
                "Engine and contracts must stay platform-neutral. Put platform I/O in adapters.",
            },
          ],
        },
      ],
    },
  },
);
