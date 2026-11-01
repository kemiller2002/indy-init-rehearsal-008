// Owner: ui (WI-R8-UIP0). UI-024: user-oriented terminology (presentation-language contract).
import { expect, test } from "../support/fixtures.js";

const workspace = "#/investigations/new";
const start = (page) => page.locator("[data-causa-workspace]").getByRole("button", { name: "Start investigation", exact: true }).click();
const visibleText = (page) => page.evaluate(() => document.body.innerText);

test("UI-024-O3", async ({ page, causa }) => {
  // The default labels replace their technical predecessors everywhere a person reads them.
  const replaced = ["Historical investigations", "Fetching and validating the investigation", "Expected lifecycle", "Replayed state", "Open a sandbox copy", "Ask the engine"];
  for (const route of ["#/", workspace, "#/investigations/0f8fad5b-d9cb-469f-a165-70867728950e?repo=kemiller2002/causa-data"]) {
    await causa.open(route);
    const text = await visibleText(page);
    for (const old of replaced) expect(text, `${route}: "${old}"`).not.toContain(old);
  }
  await causa.open("#/");
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Investigations");
  // The investigation's lifecycle is labelled "Status".
  await causa.open(workspace);
  await start(page);
  await expect(page.locator("[data-causa-workspace] dt", { hasText: /^Status$/ })).toBeVisible();
});

test("UI-024-O4", async ({ page, causa }) => {
  // Accurate to unsaved state: a tab-local change is never described as saved.
  await causa.open(workspace);
  await start(page);
  await page.locator("[data-causa-workspace]").getByRole("button", { name: "Record an observation", exact: true }).click();
  await expect(page.locator("[data-causa-save-state]")).toHaveText("Unsaved");
  await expect(page.getByRole("main")).toContainText("Not saved.");
  expect(await page.getByRole("main").innerText()).not.toMatch(/\bSaved\b|saved to GitHub|committed/);
});

test("UI-024-O5", async ({ page, causa }) => {
  // Technical codes belong in Administration: the workspace shows no kernel error codes or type names.
  await causa.open(workspace);
  const text = await page.getByRole("main").innerText();
  expect(text).not.toMatch(/\((illegal-transition|version-conflict|duplicate|[a-z]+-[a-z-]+)\)/);
  expect(text).not.toMatch(/\b(StartInvestigation|RecordObservation|ActivateInvestigation|RecordHypothesis|RecordConclusion|ResolveInvestigation)\b/);
});
