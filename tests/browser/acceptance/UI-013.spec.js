// Owner: ui (WI-R8-UIP0). UI-013: cross-view consistency.
import { expect, test } from "../support/fixtures.js";

test("UI-013-O1", async ({ page, causa }) => {
  // Every region of the workspace reads the same projection version (ADRP-004): after each accepted command the
  // workspace version, the timeline and the inspector agree.
  await causa.open("#/investigations/new");
  const ws = page.locator("[data-causa-workspace]");
  for (const [name, version] of [["Start investigation", "1"], ["Record an observation", "2"], ["Activate", "3"]]) {
    await ws.getByRole("button", { name, exact: true }).click();
    await expect(page.locator('[data-text="projectionVersion"]').first()).toHaveText(version);
    await expect(page.locator('[data-text="timelineCount"]').first()).toHaveText(version);
    await expect(page.locator('[data-text="inspectorVersion"]').first()).toHaveText(version);
  }
});

test("UI-013-O4", async ({ page, causa }) => {
  // Deep links resolve or fail with a useful recovery state: an investigation link that has not been loaded offers a
  // way to load it, and a link to nothing offers a way back.
  await causa.open("#/nonsense");
  await expect(page.getByRole("link", { name: "Go to Investigations" })).toBeVisible();
  await causa.open("#/investigations/0f8fad5b-d9cb-469f-a165-70867728950e?repo=kemiller2002/causa-data&tab=evidence");
  const main = page.getByRole("main");
  await expect(main).toContainText("has not been loaded");
  await expect(main.getByRole("button", { name: /Connect|Open|Load/ }).or(main.getByRole("link", { name: /Connect|Open|Load/ }))).toBeVisible();
});
