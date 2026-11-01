// Owner: ui-2 (T29). UI-019: the dedicated Settings page. It lists only what this version supports, with effective
// value and scope, offers no control it cannot honour, and keeps a preference apart from repository access the provider checks.
import { expect, test } from "../support/fixtures.js";

const settings = (page) => page.locator("section[aria-labelledby='causa-settings-heading']");
const setting = (page, key) => settings(page).locator(`tr[data-setting="${key}"]`);

test("UI-019-O1", async ({ page, causa, failures }) => {
  await causa.open("#/settings");
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Settings");
  await expect(settings(page)).toBeVisible();
  // Distinct from Administration and from investigation views.
  await expect(page.locator("section[aria-labelledby='causa-admin-heading']")).toBeHidden();
  await expect(page.getByRole("navigation", { name: "Investigation" })).toBeHidden();
  expect(failures).toEqual([]);
});

test("UI-019-O2", async ({ page, causa }) => {
  await causa.open("#/settings");
  await expect(settings(page).locator("tr[data-setting]")).toHaveCount(2);
  await expect(setting(page, "appearance").locator("th")).toHaveText("Appearance");
  await expect(setting(page, "access").locator("th")).toHaveText("Repository access");
});

test("UI-019-O3", async ({ page, causa }) => {
  await causa.open("#/settings");
  // Repository access is what the provider checks, not a saved preference; with no repository opened, none is claimed.
  await expect(setting(page, "access").locator("td").nth(0)).toHaveText("None: no repository has been opened in this tab");
  await expect(setting(page, "access").locator("td").nth(2)).toContainText("Not a saved preference");
});

test("UI-019-O4", async ({ page, causa }) => {
  await causa.open("#/settings");
  await expect(settings(page).locator("tr[data-setting]").first()).toBeVisible();
  await expect(settings(page).locator("button, input, select, textarea, form, [data-event]")).toHaveCount(0);
});

test("UI-019-O5", async ({ page, causa }) => {
  await causa.open("#/settings");
  for (const key of ["appearance", "access"]) {
    await expect(setting(page, key).locator("td").nth(0)).not.toBeEmpty();
    await expect(setting(page, key).locator("td").nth(1)).toHaveText(/^(Session|Device|Account)\b/);
  }
  // Nothing was saved to browser storage for these settings, and no token is shown.
  const stored = await page.evaluate(() => [Object.keys(localStorage), Object.keys(sessionStorage)]);
  expect(stored).toEqual([[], []]);
  await expect(setting(page, "access").locator("td").nth(2)).toContainText("Credentials are never stored or shown");
});

test("UI-019-O6", async ({ page, causa }) => {
  await causa.open("#/settings");
  // Provider authentication stays in the connection flow and its read-only/write scope contract: Settings neither asks
  // for a credential nor claims a scope the provider has not verified.
  await expect(settings(page).locator('input[type="password"], [autocomplete="current-password"]')).toHaveCount(0);
  await expect(setting(page, "access").locator("td").nth(0)).not.toContainText(/read|write/);
  await expect(setting(page, "access").locator("td").nth(1)).toHaveText("Session (this tab only)");
});
