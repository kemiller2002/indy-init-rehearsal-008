// Owner: integration (T11, DEP-CURRENT-004): Limen startup and Forma render of the F# WebAssembly engine on the
// locked set, from the deployable site exactly as it is published (dist/).
import { expect, sitePath, test } from "./support/fixtures.js";

test("the loading shell renders before the engine starts", async ({ page }) => {
  // Hold the runtime download so the shell is observed before WebAssembly can initialise (WASM-LOAD-001).
  let release;
  const held = new Promise((resolve) => (release = resolve));
  await page.route("**/_framework/dotnet.native.wasm", async (route) => {
    await held;
    await route.continue();
  });
  await page.goto(sitePath);
  await expect(page.getByRole("status")).toHaveText("Starting Causa…");
  await expect(page.locator("html")).toHaveAttribute("data-engine", "loading");
  await expect(page.locator("[data-causa-app]")).toBeHidden();
  release();
  await expect(page.locator("html")).toHaveAttribute("data-engine", "ready", { timeout: 30_000 });
});

test("the F# engine boots through Limen and projects the shell view", async ({ page, failures, causa }) => {
  await causa.open();
  const app = page.locator("[data-causa-app]");
  await expect(app).toBeVisible();
  await expect(app.locator('[data-text="status"]')).toHaveText("Causa engine ready");
  await expect(app.locator('[data-text="engine"]')).toHaveText("causa");
  await expect(app.locator('[data-text="path"]')).toHaveText(sitePath);
  await expect(page.locator("[data-causa-loading]")).toBeHidden();
  const limen = await page.evaluate(() => globalThis.__ECHELON_RUNTIME__?.libraries?.limen?.version ?? null);
  expect(limen).toBe("0.9.1");
  expect(failures).toEqual([]);
});

test("Forma renders on the locked set", async ({ page, causa }) => {
  await causa.open();
  const forma = await page.evaluate(() => ({
    target: getComputedStyle(document.documentElement).getPropertyValue("--ef-layout-target-min").trim(),
    surface: getComputedStyle(document.documentElement).getPropertyValue("--ef-color-surface-primary").trim()
  }));
  expect(forma.target).not.toBe("");
  expect(forma.surface).not.toBe("");
});

test("a runtime that cannot download shows the failure and Retry", async ({ page }) => {
  await page.route("**/_framework/dotnet.native.wasm", (route) => route.abort());
  await page.goto(sitePath);
  await expect(page.locator("html")).toHaveAttribute("data-engine", "failed", { timeout: 30_000 });
  await expect(page.getByRole("alert")).toContainText("Causa could not start");
  const retry = page.getByRole("button", { name: "Retry" });
  await expect(retry).toBeVisible();
  await page.unroute("**/_framework/dotnet.native.wasm");
  await retry.click();
  await expect(page.locator("html")).toHaveAttribute("data-engine", "ready", { timeout: 30_000 });
});
