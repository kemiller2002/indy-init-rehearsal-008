// Owner: integration. Real-browser suites for Causa.
//
// The server serves the repository root: the scaffold pages under src/kernel/ reach into node_modules/, and the
// deployable site is dist/ (dotnet fsi build.fsx site), opened at /dist/index.html exactly as it is published.
// The webServer command assembles dist/ first, so `npx playwright test` is self-contained.
import { existsSync } from "node:fs";
import { defineConfig, devices } from "@playwright/test";

const port = 4321;
const origin = `http://127.0.0.1:${port}`;

// Some environments ship a Chromium that Playwright did not download itself. Where that binary exists it is used
// as-is; everywhere else Playwright resolves its own, so CI needs no special case.
const preinstalledChromium = "/opt/pw-browsers/chromium";
const chromiumLaunch = existsSync(preinstalledChromium) ? { executablePath: preinstalledChromium } : {};

export default defineConfig({
  testDir: "./tests/browser",
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: 0,
  timeout: 60_000,
  reporter: process.env.CI ? [["github"], ["list"], ["json", { outputFile: "test-results/playwright.json" }]] : [["list"]],
  use: { baseURL: origin, trace: "retain-on-failure" },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"], launchOptions: chromiumLaunch } }],
  webServer: {
    command: `dotnet fsi build.fsx site && python3 -m http.server ${port} --bind 127.0.0.1`,
    url: `${origin}/dist/index.html`,
    reuseExistingServer: !process.env.CI,
    timeout: 300_000
  }
});
