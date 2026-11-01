// Owner: ui (T16). UI-020: navigation and information architecture.
import { expect, test } from "../support/fixtures.js";
import { navigationReachable } from "../support/layout-assertions.mjs";

const investigation = "#/investigations/0f8fad5b-d9cb-469f-a165-70867728950e";
const persistent = (page) => page.locator("nav.ef-navigation-shell__persistent");

test("UI-020-O1", async ({ page, causa }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await causa.open();
  const nav = page.getByRole("navigation", { name: "Primary" }).filter({ visible: true });
  for (const [name, hash, title] of [
    ["Investigations", "#/", "Investigations"],
    ["Administration", "#/administration", "Administration"],
    ["Settings", "#/settings", "Settings"]
  ]) {
    const link = nav.getByRole("link", { name, exact: true });
    await expect(link).toHaveAttribute("href", hash);
    await link.click();
    await expect(page.getByRole("heading", { level: 1 })).toHaveText(title);
    await expect(page).toHaveTitle(`${title} · Causa`);
  }
});

test("UI-020-O2", async ({ page, causa }) => {
  await causa.open(investigation);
  await expect(page.getByRole("navigation", { name: "Investigation" })).toBeVisible();
  for (const hash of ["#/administration", "#/settings", "#/"]) {
    await causa.open(hash);
    await expect(page.getByRole("navigation", { name: "Investigation" })).toBeHidden();
  }
  await causa.open("#/administration");
  await expect(page.getByRole("heading", { level: 2, name: "Operations" })).toBeVisible();
  await causa.open("#/settings");
  await expect(page.getByRole("heading", { level: 2, name: "Preferences" })).toBeVisible();
});

test("UI-020-O3", async ({ page, causa }) => {
  for (const viewport of [{ width: 320, height: 640 }, { width: 820, height: 1180 }, { width: 1180, height: 820 }, { width: 1440, height: 900 }]) {
    await page.setViewportSize(viewport);
    await causa.open("#/administration");
    expect(await navigationReachable(page), `reachable at ${viewport.width}`).toEqual([]);
    const current = page.locator('nav[aria-label="Primary"] [aria-current="page"]').filter({ visible: true });
    const compact = page.locator(".ef-navigation-shell__compact");
    if (await compact.isVisible()) await compact.locator("summary").click();
    await expect(current).toHaveText("Administration");
    // The active page is identified by more than colour: its weight differs from the other links.
    const weights = await page
      .locator('nav[aria-label="Primary"] a')
      .filter({ visible: true })
      .evaluateAll((links) => links.map((a) => [a.getAttribute("aria-current"), getComputedStyle(a).fontWeight]));
    const active = weights.filter(([c]) => c === "page").map(([, w]) => w);
    const others = weights.filter(([c]) => c !== "page").map(([, w]) => w);
    expect(active.length).toBe(1);
    expect(others).not.toContain(active[0]);
  }
});

test("UI-020-O4", async ({ page, causa }) => {
  const titles = [];
  for (const hash of ["#/", "#/administration", "#/settings", investigation]) {
    await causa.open(hash);
    titles.push(await page.getByRole("heading", { level: 1 }).textContent());
    await expect(persistent(page).locator('[aria-current="page"]')).toHaveCount(1);
  }
  expect(new Set(titles).size).toBe(titles.length);
});
