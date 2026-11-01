// Owner: ui (WI-R8-UIP0). UI-017: investigation-facing copy is domain-focused. The visible copy of the routine
// investigation pages carries no implementation prose: no catalog/replay paragraph, storage schema version, catalog
// identifier, commit SHA, replay or validation mechanics, framework version or infrastructure detail.
import { expect, test } from "../support/fixtures.js";

const routine = [
  "#/",
  "#/investigations/new",
  "#/investigations/0f8fad5b-d9cb-469f-a165-70867728950e?repo=kemiller2002/causa-data",
  "#/investigations/0f8fad5b-d9cb-469f-a165-70867728950e?repo=kemiller2002/causa-data&tab=causal-analysis",
  "#/investigations/0f8fad5b-d9cb-469f-a165-70867728950e?repo=kemiller2002/causa-data&tab=timeline"
];
// Visible copy only: visually hidden live-region text (ef-visually-hidden) is excluded.
const visibleText = (page) => page.evaluate(() => { const c = document.body.cloneNode(true); c.querySelectorAll(".ef-visually-hidden, [hidden]").forEach((e) => e.remove()); document.body.append(c); c.style.position = "absolute"; const t = c.innerText; c.remove(); return t; });
const implementationProse = [
  /historical-v1/i, /active catalog/i, /\breplay/i, /\bschema\b/i, /\b[0-9a-f]{40}\b/, /\bcommit [0-9a-f]{7,}\b/i,
  /\bWebAssembly\b|\bWASM\b|\.NET\b|\bBlazor\b|\bLimen\b|\bF#/i, /\bv?\d+\.\d+\.\d+\b/
];

test("UI-017-O2", async ({ page, causa }) => {
  for (const route of routine) {
    await causa.open(route);
    if (route === "#/investigations/new") {
      await page.locator("[data-causa-workspace]").getByRole("button", { name: "Start investigation", exact: true }).click();
    }
    const text = await visibleText(page);
    for (const pattern of implementationProse) expect(text, `${route} shows ${pattern}`).not.toMatch(pattern);
  }
});

test("UI-017-O3", async ({ page, causa }) => {
  await causa.open("#/");
  expect(await visibleText(page)).not.toContain("The active catalog historical-v1");
});

test("UI-017-O1", async ({ page, causa }) => {
  // Investigation pages present user tasks and actionable statuses: the start screen names the task and its action,
  // the workspace shows the investigation's status and the next actions in a person's words.
  await causa.open("#/");
  await expect(page.getByRole("main").getByText("Start an investigation", { exact: true }).first()).toBeVisible();
  await causa.open("#/investigations/new");
  const ws = page.locator("[data-causa-workspace]");
  await ws.getByRole("button", { name: "Start investigation", exact: true }).click();
  await expect(page.locator("[data-causa-save-state]")).toHaveText("Unsaved");
  await expect(ws).toContainText("Next: Record an observation");
});
