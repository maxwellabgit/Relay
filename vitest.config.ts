import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    projects: [
      {
        test: {
          name: "unit",
          include: [
            "packages/**/*.unit.test.ts",
            "adapters/**/*.unit.test.ts",
            "apps/**/*.unit.test.ts",
            "tools/**/*.unit.test.ts",
          ],
        },
      },
      {
        test: {
          name: "integration",
          include: ["**/*.integration.test.ts"],
          testTimeout: 20_000,
          maxWorkers: 2,
          fileParallelism: false,
        },
      },
      {
        test: {
          name: "replay",
          include: ["**/*.replay.test.ts"],
        },
      },
      {
        test: {
          name: "privacy",
          include: ["**/*.privacy.test.ts"],
          testTimeout: 20_000,
        },
      },
      {
        test: {
          name: "architecture",
          include: ["**/*.architecture.test.ts"],
        },
      },
    ],
  },
});
