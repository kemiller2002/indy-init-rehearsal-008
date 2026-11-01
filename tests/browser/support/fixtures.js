// Owner: integration. Shared Playwright fixtures. Import `test` and `expect` from here instead of @playwright/test.
//
//   failures   every HTTP error, failed request and uncaught script error the page produced (assert it is [])
//   causa      helpers for the deployable site: open(path) loads /dist/index.html (plus an optional route) and
//              waits until the F# engine has booted through Limen (html[data-engine="ready"])
import { test as base, expect } from "@playwright/test";

export const sitePath = "/dist/index.html";

export const test = base.extend({
  failures: async ({ page }, use) => {
    const failures = [];
    page.on("response", (response) => {
      if (response.status() >= 400) failures.push(`${response.status()} ${response.url()}`);
    });
    page.on("requestfailed", (request) => failures.push(`failed ${request.url()}`));
    page.on("pageerror", (error) => failures.push(`script error: ${error.message}`));
    await use(failures);
  },
  causa: async ({ page }, use) => {
    await use({
      open: async (suffix = "") => {
        await page.goto(`${sitePath}${suffix}`);
        await expect(page.locator("html")).toHaveAttribute("data-engine", "ready", { timeout: 30_000 });
      }
    });
  }
});

export { expect };
