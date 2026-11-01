// Owner: ui (WI-R8-T22). PERSIST-033 Workspace connection and unsaved summary, on the deployable site. The header shows
// the connection state and, when an investigation of the tab is not Saved, how many with a link; it never contradicts
// the workspace. PERSIST-033-O3 (the PAT-02/05/07 scenarios with a connected repository) is NOT_RUN in this build.
import { expect, test } from "../support/fixtures.js";

test("PERSIST-033-O1", async ({ page, causa, failures }) => {
  await causa.open("#/investigations/new");
  await expect(page.locator("[data-causa-connection]")).toHaveText("Not connected");
  await expect(page.locator("[data-causa-header-unsaved]")).toBeHidden();
  await page.locator("[data-causa-workspace]").getByRole("button", { name: "Start investigation", exact: true }).click();
  const summary = page.locator("[data-causa-header-unsaved]");
  await expect(summary).toBeVisible();
  await expect(summary).toHaveText("1 investigation in this tab is not saved.");
  await expect(summary.getByRole("link")).toHaveAttribute("href", "#/investigations/new");
  expect(failures).toEqual([]);
});

test("PERSIST-033-O2", async ({ page, causa }) => {
  await causa.open("#/investigations/new");
  const workspace = page.locator("[data-causa-workspace]");
  // Empty workspace: no unsaved summary. One investigation not Saved: the header says exactly one, on every route.
  await expect(workspace.locator("[data-causa-lifecycle]")).toHaveText("No investigation");
  await expect(page.locator("[data-causa-header-unsaved]")).toBeHidden();
  await workspace.getByRole("button", { name: "Start investigation", exact: true }).click();
  await expect(workspace.locator("[data-causa-lifecycle]")).toHaveText("Draft");
  for (const hash of ["#/", "#/settings", "#/investigations/new"]) {
    await page.evaluate((h) => { location.hash = h; }, hash);
    await expect(page.locator("[data-causa-header-unsaved]"), hash).toHaveText("1 investigation in this tab is not saved.");
  }
  await expect(page.locator("[data-causa-header-offline]")).toBeHidden();
});
