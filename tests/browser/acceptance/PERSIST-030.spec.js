// Owner: ui (WI-R8-T22). PERSIST-030 Forma save-state indicator, on the deployable site. In this build the browser can
// reach only the states of a tab with no repository connected (no investigation, Unsaved); the other five states'
// words, actions and politeness are verified against the projection in Causa.Contract.Tests Save.ConsumerUi
// (IndicatorFollowsMachine) and are NOT_RUN here until Connect/save is wired (ADRP-002-O4).
import { expect, test } from "../support/fixtures.js";

const indicator = (page) => page.locator("[data-causa-save-indicator]");
const start = (page) => page.locator("[data-causa-workspace]").getByRole("button", { name: "Start investigation", exact: true }).click();

test("PERSIST-030-O1", async ({ page, causa, failures }) => {
  await causa.open("#/investigations/new");
  // No investigation displayed: no indicator. An investigation displayed: the indicator, from Forma components.
  await expect(indicator(page)).toBeHidden();
  await start(page);
  await expect(indicator(page)).toBeVisible();
  await expect(indicator(page)).toHaveClass(/ef-operation-status/);
  await expect(indicator(page).locator(".ef-status-lozenge")).toHaveCount(1);
  await expect(indicator(page).locator(".ef-operation-status__actions")).toHaveCount(1);
  expect(failures).toEqual([]);
});

test("PERSIST-030-O3", async ({ page, causa }) => {
  await causa.open("#/investigations/new");
  await start(page);
  const label = indicator(page).locator("[data-causa-save-state]");
  await expect(label).toHaveText("Unsaved");
  const seven = ["Saved", "Unsaved", "Saving", "Offline", "Conflict", "Unknown commit outcome", "Save failed"];
  const shown = await indicator(page).locator(".ef-status-lozenge").allTextContents();
  expect(shown.filter((t) => seven.includes(t.trim()))).toEqual(["Unsaved"]);
});

test("PERSIST-030-O4", async ({ page, causa }) => {
  await causa.open("#/investigations/new");
  await start(page);
  // Named in text and marked with the lozenge glyph (state attribute + symbol), never by colour alone.
  await expect(indicator(page).locator("[data-causa-save-state]")).toHaveText("Unsaved");
  await expect(indicator(page).locator("[data-causa-save-state]")).toHaveAttribute("data-state", "attention");
  await expect(indicator(page).locator(".ef-operation-status__symbol")).not.toHaveText("");
});

test("PERSIST-030-O6", async ({ page, causa }) => {
  await causa.open("#/investigations/new");
  const polite = page.locator("[data-causa-save-polite]");
  const assertive = page.locator("[data-causa-save-assertive]");
  await expect(polite).toHaveAttribute("aria-live", "polite");
  await expect(assertive).toHaveAttribute("aria-live", "assertive");
  await start(page);
  // Unsaved is announced politely; nothing is announced assertively.
  await expect(polite).toContainText("Unsaved.");
  await expect(assertive).toHaveText("");
});

test("PERSIST-030-O7", async ({ page, causa }) => {
  for (const viewport of [{ width: 320, height: 640 }, { width: 768, height: 1024 }, { width: 1024, height: 768 }]) {
    await page.setViewportSize(viewport);
    await causa.open("#/investigations/new");
    await start(page);
    const box = await indicator(page).boundingBox();
    expect(box, JSON.stringify(viewport)).not.toBeNull();
    expect(box.x).toBeGreaterThanOrEqual(0);
    expect(box.x + box.width).toBeLessThanOrEqual(viewport.width + 1);
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    expect(overflow, `horizontal overflow at ${viewport.width}px`).toBeLessThanOrEqual(0);
  }
});
