// Owner: ui (WI-R8-UIP0). UI-025: the investigation overview is the landing view. (The workspace also holds the
// report preview, WI-R8-T26, whose headings repeat the region names: the region heading is the first.)
import { expect, test } from "../support/fixtures.js";

const id = "0f8fad5b-d9cb-469f-a165-70867728950e";

test("UI-025-O1", async ({ page, causa }) => {
  // Opening an investigation lands on its Overview.
  await causa.open(`#/investigations/${id}?repo=kemiller2002/causa-data`);
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Overview");
  await expect(page.getByRole("navigation", { name: "Investigation" }).getByRole("link", { name: "Overview" })).toHaveAttribute("aria-current", "page");
  // The overview of an investigation in this tab answers, from the projection: what happened (observations), findings
  // (conclusions), evidence, unresolved questions (hypotheses) and the next authorized actions.
  await causa.open("#/investigations/new");
  const ws = page.locator("[data-causa-workspace]");
  await ws.getByRole("button", { name: "Start investigation", exact: true }).click();
  await ws.getByRole("button", { name: "Record an observation", exact: true }).click();
  for (const heading of ["Observations", "Hypotheses", "Evidence", "Conclusions", "Blockers and next actions"]) {
    await expect(ws.getByRole("heading", { name: heading, exact: true }).first()).toBeVisible();
  }
  await expect(ws).toContainText("Observation 1 (version 2)");
  await expect(ws).toContainText(/Next: .*Activate/);
});

test("UI-025-O2", async ({ page, causa }) => {
  // Unknown information is shown as unknown, never as an invented summary or as zero counts.
  await causa.open(`#/investigations/${id}?repo=kemiller2002/causa-data`);
  const main = page.getByRole("main");
  await expect(main).toContainText("This investigation has not been loaded in this tab.");
  expect(await main.innerText()).not.toMatch(/\b0 (hypotheses|observations|evidence)\b|Observations\s*\n?\s*0/i);
});

test("UI-025-O4", async ({ page, causa }) => {
  // No generated interpretation presented as fact: without recorded conclusions there is no finding.
  await causa.open("#/investigations/new");
  const ws = page.locator("[data-causa-workspace]");
  await ws.getByRole("button", { name: "Start investigation", exact: true }).click();
  await expect(ws).toContainText("No conclusion recorded yet.");
  expect(await page.getByRole("main").innerText()).not.toMatch(/root cause (is|was)|the cause (is|was)|\bAI\b|generated summary/);
});
