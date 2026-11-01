// Owner: ui (WI-R8-UIP0). UI-001: the investigation dashboard (S01), on the deployable site.
// O1 needs the explicit "Connect data repository" action (EXT-DATA-002) before Connect and the four demonstration
// investigations (EXT-DATA-013) after it. This build has no Connect action yet, so O1 fails here: an unmet obligation,
// not a test fault. O2-O4 (cards, counts, severity/owner, next legal action) need a connected catalog and are not
// specified until Connect is wired.
import { expect, test } from "../support/fixtures.js";

test("UI-001-O1", async ({ page, causa, failures }) => {
  await causa.open("#/");
  // Before Connect: an accessible, empty, disconnected workspace...
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Investigations");
  await expect(page.locator(".causa-sync .ef-status-lozenge")).toHaveText("Not connected");
  await expect(page.getByRole("main")).not.toContainText(/Apollo|Challenger|lightbulb/i);
  // ...with the explicit Connect data repository action.
  await expect(page.getByRole("button", { name: "Connect data repository" })).toBeVisible();
  expect(failures).toEqual([]);
});

test("UI-001-O6", async ({ page, causa }) => {
  // Opening an investigation preserves its identity and context: the route's investigation and repository are the
  // ones the page names, on every investigation destination.
  const id = "0f8fad5b-d9cb-469f-a165-70867728950e";
  for (const tab of ["", "&tab=causal-analysis", "&tab=evidence", "&tab=timeline"]) {
    await causa.open(`#/investigations/${id}?repo=kemiller2002/causa-data${tab}`);
    const header = page.locator(".causa-investigation-header");
    await expect(header.getByRole("link", { name: "Investigation 0f8fad5b" })).toHaveAttribute("href", `#/investigations/${id}?repo=kemiller2002/causa-data`);
    await expect(header).toContainText("Repository: kemiller2002/causa-data");
  }
});
