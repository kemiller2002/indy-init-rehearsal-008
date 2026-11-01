// Owner: ui (WI-R8-UIP0). UI-031: system detail moved out of the workflow is kept in Administration, not deleted.
import { expect, test } from "../support/fixtures.js";

test("UI-031-O2", async ({ page, causa }) => {
  await causa.open("#/administration");
  const main = page.getByRole("main");
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Administration");
  // Connection health: the tab's connection state, in Administration's own content (not only the global header).
  await expect(main).toContainText(/Not connected/);
  // Build and component versions: the deployed build's commit and the Forma design system version.
  await expect(main).toContainText(/\b[0-9a-f]{7,40}\b/);
  await expect(main).toContainText(/Forma|design-system/i);
  await expect(main).toContainText(/\d+\.\d+\.\d+/);
});

test("UI-031-O4", async ({ page, causa }) => {
  // Settings holds only user-adjustable options: no read-only technical facts (versions, commits, provider details).
  await causa.open("#/settings");
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Settings");
  const text = await page.getByRole("main").innerText();
  expect(text).not.toMatch(/\b[0-9a-f]{7,40}\b|\bv?\d+\.\d+\.\d+\b|api\.github\.com|kemiller2002\//i);
});

test("UI-031-O5", async ({ page, causa }) => {
  // Credentials are never exposed: no page offers a credential back, and no browser store holds one.
  for (const route of ["#/", "#/administration", "#/settings"]) {
    await causa.open(route);
    expect(await page.content(), route).not.toMatch(/gh[pousr]_[A-Za-z0-9]{20,}|github_pat_/);
    expect(await page.evaluate(() => localStorage.length + sessionStorage.length + document.cookie.length)).toBe(0);
  }
});
