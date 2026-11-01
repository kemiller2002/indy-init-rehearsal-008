// Owner: ui (T16). UI-029: two-level navigation and responsive application shell (FL-09).
import { expect, test } from "../support/fixtures.js";
import { navigationCollapses, navigationReachable } from "../support/layout-assertions.mjs";

const investigation = "#/investigations/0f8fad5b-d9cb-469f-a165-70867728950e?repo=kemiller2002/causa-data";
const viewports = [
  { width: 320, height: 640 },
  { width: 820, height: 1180 },
  { width: 1180, height: 820 },
  { width: 1440, height: 900 }
];
const primary = (page) => page.locator(".ef-navigation-shell > nav.ef-navigation-shell__persistent, .ef-navigation-shell__compact > nav").filter({ visible: true });

test("UI-029-O1", async ({ page, causa }) => {
  for (const viewport of viewports) {
    await page.setViewportSize(viewport);
    await causa.open(investigation);
    expect(await navigationCollapses(page), `collapse at ${viewport.width}`).toEqual([]);
    expect(await navigationReachable(page), `reachable at ${viewport.width}`).toEqual([]);
    const compact = page.locator(".ef-navigation-shell__compact");
    if (await compact.isVisible()) await compact.locator("summary").click();
    for (const name of ["Investigations", "Administration", "Settings"]) {
      await expect(primary(page).getByRole("link", { name, exact: true })).toBeVisible();
    }
  }
});

test("UI-029-O2", async ({ page, causa }) => {
  await causa.open(investigation);
  const secondary = page.getByRole("navigation", { name: "Investigation" });
  await expect(secondary).toBeVisible();
  const names = await secondary.getByRole("link").allTextContents();
  expect(names.map((n) => n.trim())).toEqual(["Overview", "Causal Analysis", "Evidence", "Hypotheses", "Timeline", "Corrective Actions"]);
  await secondary.getByRole("link", { name: "Timeline" }).click();
  await expect(page).toHaveURL(/#\/investigations\/0f8fad5b-d9cb-469f-a165-70867728950e\?repo=kemiller2002\/causa-data&tab=timeline$/);
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Timeline");
  await expect(secondary.getByRole("link", { name: "Timeline" })).toHaveAttribute("aria-current", "page");
});

test("UI-029-O3", async ({ page, causa }) => {
  for (const viewport of [{ width: 320, height: 640 }, { width: 820, height: 1180 }, { width: 1440, height: 900 }]) {
    await page.setViewportSize(viewport);
    await causa.open("#/settings");
    const compact = page.locator(".ef-navigation-shell__compact");
    if (await compact.isVisible()) await compact.locator("summary").click();
    const nav = primary(page);
    await expect(nav.locator('[aria-current="page"]')).toHaveText("Settings");
    const admin = nav.getByRole("link", { name: "Administration" });
    await admin.focus();
    await page.keyboard.press("Enter");
    await expect(page.getByRole("heading", { level: 1 })).toHaveText("Administration");
    await expect(nav.locator('[aria-current="page"]')).toHaveText("Administration");
    // Focus stays on the control the person used; navigation never drops it to the document.
    await expect(admin).toBeFocused();
  }
});

test("UI-029-O4", async ({ page, causa }) => {
  await causa.open(investigation);
  const primaryNav = page.locator("nav.ef-navigation-shell__persistent");
  const secondary = page.getByRole("navigation", { name: "Investigation" });
  await expect(primaryNav).toHaveAttribute("aria-label", "Primary");
  // Global destinations live only in the primary navigation, investigation destinations only in the secondary one.
  await expect(secondary.getByRole("link", { name: "Settings" })).toHaveCount(0);
  await expect(secondary.getByRole("link", { name: "Administration" })).toHaveCount(0);
  await expect(primaryNav.getByRole("link", { name: "Evidence" })).toHaveCount(0);
  expect(await primaryNav.evaluate((n, s) => n.contains(s), await secondary.elementHandle())).toBe(false);
  await causa.open("#/settings");
  await expect(secondary).toBeHidden();
});
