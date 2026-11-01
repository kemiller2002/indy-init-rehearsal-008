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

test("UI-013-O2", async ({ page, causa }) => {
  // WI-R8-FINALUI (ui). A successful command refreshes every dependent view from the new projection in one update:
  // a MutationObserver samples the workspace after every DOM change, and no sample ever mixes two versions (the
  // version, timeline count, inspector version, lifecycle and Activate availability always belong together).
  await causa.open("#/investigations/new");
  await page.evaluate(() => {
    const read = (sel) => document.querySelector(sel)?.textContent?.trim() ?? "";
    const sample = () => ({
      projection: read('[data-text="projectionVersion"]'),
      timeline: read('[data-text="timelineCount"]'),
      inspector: read('[data-text="inspectorVersion"]'),
      lifecycle: read("[data-causa-workspace] [data-causa-lifecycle]"),
      activate: read('[data-causa-workspace] [data-causa-capability="ActivateInvestigation"]')
    });
    window.__causaSamples = [];
    new MutationObserver(() => window.__causaSamples.push(sample())).observe(document.body, { subtree: true, childList: true, characterData: true, attributes: true });
  });
  const ws = page.locator("[data-causa-workspace]");
  for (const [name, version] of [["Start investigation", "1"], ["Record an observation", "2"], ["Activate", "3"]]) {
    await ws.getByRole("button", { name, exact: true }).click();
    await expect(page.locator('[data-text="projectionVersion"]').first()).toHaveText(version);
    await expect(page.locator('[data-text="inspectorVersion"]').first()).toHaveText(version);
  }
  await expect(ws.locator("[data-causa-lifecycle]")).toHaveText("Active");
  // Let any trailing microtask flush, then examine every sample taken.
  await page.evaluate(() => new Promise((r) => requestAnimationFrame(() => r())));
  const samples = await page.evaluate(() => window.__causaSamples);
  expect(samples.length).toBeGreaterThan(0);
  const seen = new Set();
  for (const s of samples.filter((x) => x.projection !== "" && x.projection !== "0")) {
    // Version-bearing regions agree in every sample.
    expect(s.timeline, JSON.stringify(s)).toBe(s.projection);
    expect(s.inspector, JSON.stringify(s)).toBe(s.projection);
    // Dependent state matches its version: Activate becomes available exactly with the observation (v2), and the
    // lifecycle becomes Active exactly with activation (v3).
    if (s.projection === "1") expect(s.activate, JSON.stringify(s)).toContain("Unavailable");
    if (s.projection === "2") expect(s.activate, JSON.stringify(s)).toBe("Available");
    if (s.projection === "1" || s.projection === "2") expect(s.lifecycle, JSON.stringify(s)).toBe("Draft");
    if (s.projection === "3") expect(s.lifecycle, JSON.stringify(s)).toBe("Active");
    seen.add(s.projection);
  }
  expect([...seen]).toEqual(expect.arrayContaining(["1", "2", "3"]));
});
