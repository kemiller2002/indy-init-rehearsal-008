// Owner: ui (T16). UI-014: visual identity and navigation.
import { expect, sitePath, test } from "../support/fixtures.js";

const id = "0f8fad5b-d9cb-469f-a165-70867728950e";

test("UI-014-O1", async ({ page, causa }) => {
  await causa.open();
  // Naming and page titles: "<screen> · Causa" on every destination.
  for (const [hash, title] of [["#/", "Investigations · Causa"], ["#/administration", "Administration · Causa"], ["#/settings", "Settings · Causa"], [`#/investigations/${id}`, "Overview · Investigation · Causa"]]) {
    await causa.open(hash);
    await expect(page).toHaveTitle(title);
  }
  // Favicon and icon: one Causa mark, available before WebAssembly (an inline SVG, no remote asset).
  const icon = await page.locator('link[rel="icon"]').getAttribute("href");
  expect(icon).toMatch(/^data:image\/svg\+xml,/);
  await expect(page.locator(".causa-masthead .ef-site-header__name")).toHaveText("Causa");
  await expect(page.locator(".causa-masthead svg.causa-mark")).toBeVisible();
  // Accessible status colours: the sync indicator is a Forma status lozenge with a text label.
  const sync = page.locator(".causa-sync .ef-status-lozenge");
  await expect(sync).toHaveText("Not connected");
  await expect(sync).toHaveAttribute("data-state", "unknown");
});

test("UI-014-O2", async ({ page }) => {
  // WASM-LOAD-005 first: a transport failure (503) shows a recoverable error and a keyboard-operable Retry.
  let mode = "fail";
  let release;
  const held = new Promise((resolve) => (release = resolve));
  await page.route("**/_framework/dotnet.native.wasm", async (route) => {
    if (mode === "fail") return route.fulfill({ status: 503, body: "unavailable" });
    await held;
    return route.continue();
  });
  await page.goto(sitePath);
  await expect(page.locator("html")).toHaveAttribute("data-engine", "failed", { timeout: 30_000 });
  await expect(page.getByRole("alert")).toContainText("Causa could not start");
  await expect(page.locator(".causa-loading-indicator")).toBeHidden();
  mode = "hold";
  await page.getByRole("button", { name: "Retry" }).focus();
  await page.keyboard.press("Enter");
  // WASM-LOAD-001/002/003 on the retried start: branded shell, the mark with its text equivalent, indeterminate
  // status and no percentage, while the runtime is held back.
  await expect(page.getByRole("img", { name: "Causa" })).toBeVisible();
  await expect(page.getByRole("heading", { level: 1, name: "Causa" })).toBeVisible();
  await expect(page.locator("[data-causa-load-status]")).toHaveText("Starting Causa…");
  await expect(page.locator("html")).toHaveAttribute("data-engine", "loading");
  expect(await page.locator("[data-causa-loading]").textContent()).not.toMatch(/\d+\s*%/);
  await expect(page.locator("[data-causa-app]")).toBeHidden();
  release();
  // WASM-LOAD-004: the shell stays until the engine projects; then exactly one application is shown.
  await expect(page.locator("html")).toHaveAttribute("data-engine", "ready", { timeout: 30_000 });
  await expect(page.locator("[data-causa-loading]")).toBeHidden();
  await expect(page.getByRole("heading", { level: 1 }).filter({ visible: true })).toHaveCount(1);
});

test("UI-014-O3", async ({ page, causa }) => {
  await causa.open(`#/investigations/${id}?repo=kemiller2002/causa-data&tab=evidence`);
  const header = page.locator(".causa-investigation-header");
  await expect(header).toBeVisible();
  await expect(header.getByRole("link", { name: "Investigation 0f8fad5b" })).toHaveAttribute("href", `#/investigations/${id}?repo=kemiller2002/causa-data`);
  await expect(header).toContainText("Repository: kemiller2002/causa-data");
  await expect(page).toHaveTitle("Evidence · Investigation · Causa");
});
