// Owner: ui-2 (T29). UI-018: the dedicated Administration page, with the versions the running application actually
// loaded (VER-007). Every version is read from the loaded component itself; what cannot be known says so in words.
import { expect, test } from "../support/fixtures.js";

const admin = (page) => page.locator("section[aria-labelledby='causa-admin-heading']");
const fact = (page, key) => admin(page).locator(`tr[data-fact="${key}"]`);
const states = ["verified", "unknown", "unavailable", "failed"];

/** What the page itself loaded, read in the browser: the Echelon runtime registry. */
const registered = (page) => page.evaluate(() => {
  const libraries = window.__ECHELON_RUNTIME__?.libraries ?? {};
  return Object.fromEntries(Object.entries(libraries).map(([name, entry]) => [name, entry.version]));
});

test("UI-018-O1", async ({ page, causa, failures }) => {
  await causa.open("#/administration");
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Administration");
  await expect(admin(page)).toBeVisible();
  await expect(page.locator('nav[aria-label="Primary"] [aria-current="page"]').filter({ visible: true })).toHaveText("Administration");
  expect(failures).toEqual([]);
});

test("UI-018-O2", async ({ page, causa }) => {
  await causa.open("#/administration");
  const versions = admin(page).getByRole("table", { name: "Loaded versions" });
  await expect(versions).toBeVisible();
  // Browser libraries: exactly what registered itself on this page, never a hard-coded claim.
  await expect(fact(page, "limen")).toHaveAttribute("data-state", "verified");
  const loaded = await registered(page);
  expect(loaded.limen).toBeTruthy();
  await expect(fact(page, "limen").locator("td").nth(1)).toHaveText(loaded.limen);
  if (loaded.forma) await expect(fact(page, "forma").locator("td").nth(1)).toHaveText(loaded.forma);
  // A package that does not report its version is shown as not available, never with a version.
  for (const key of ["formaWorkflow", "folio"]) {
    await expect(fact(page, key)).toHaveAttribute("data-state", "unavailable");
    await expect(fact(page, key).locator("td").nth(1)).toHaveText(/^Not available\./);
  }
  // Engine: application version and build commit from the loaded assembly; storage and interface contracts.
  await expect(fact(page, "app")).toHaveAttribute("data-state", "verified");
  await expect(fact(page, "app").locator("td").nth(1)).toHaveText(/^\d+\.\d+\.\d+/);
  await expect(fact(page, "commit").locator("td").nth(1)).toHaveText(/^[0-9a-f]{7,40}$|^Not available\./);
  await expect(fact(page, "runtime").locator("td").nth(1)).toHaveText(/\.NET/);
  await expect(fact(page, "storage").locator("td").nth(1)).toHaveText("causa-storage 1.0.0");
  await expect(fact(page, "interfaces").locator("td").nth(1)).toHaveText("1.0.0");
  // Operational status: provider, catalog, dataset validation, domain replay.
  const status = admin(page).getByRole("table", { name: "Status" });
  for (const key of ["provider", "catalog", "dataset", "replay"]) await expect(status.locator(`tr[data-fact="${key}"]`)).toHaveCount(1);
});

test("UI-018-O3", async ({ page, causa }) => {
  await causa.open("#/administration");
  const rows = admin(page).locator("tr[data-fact]");
  await expect(fact(page, "limen")).toHaveAttribute("data-state", "verified");
  const seen = await rows.evaluateAll((trs) => trs.map((tr) => [tr.dataset.state, tr.cells[1].textContent, tr.cells[2].textContent]));
  for (const [state, stateText, value] of seen) {
    expect(states).toContain(state);
    // The state is named in words in its own cell, not by colour alone.
    expect(stateText).toBe(state);
    if (state !== "verified") expect(value).toMatch(/^(Unknown|Not available|Failed)\. /);
  }
  expect(new Set(seen.map(([s]) => s))).toEqual(new Set(["verified", "unavailable"]));
});

test("UI-018-O4", async ({ page, causa }) => {
  await causa.open("#/administration");
  await expect(fact(page, "limen")).toHaveAttribute("data-state", "verified");
  // Nothing unverified reads as passed: no connection, catalog or dataset is claimed in a tab without a repository.
  for (const key of ["provider", "catalog", "dataset", "replay"]) {
    await expect(fact(page, key)).not.toHaveAttribute("data-state", "verified");
  }
  const counts = await admin(page).locator("[data-causa-admin-summary]").textContent();
  const rows = await admin(page).locator('tr[data-state="verified"]').count();
  expect(counts).toContain(`Verified ${rows},`);
});

test("UI-018-O5", async ({ page, causa }) => {
  await causa.open("#/administration");
  await expect(fact(page, "limen")).toHaveAttribute("data-state", "verified");
  const limen = await fact(page, "limen").locator("td").nth(1).textContent();
  for (const hash of ["#/", "#/investigations/new", "#/settings"]) {
    await causa.open(hash);
    await expect(admin(page)).toBeHidden();
    // No version, build commit or diagnostic is visible outside Administration (VER-007-O1): not in the footer either.
    await expect(page.getByText("Loaded versions")).toBeHidden();
    await expect(page.getByText("causa-storage 1.0.0", { exact: true })).toBeHidden();
    const visible = await page.locator("body").innerText();
    expect(visible).not.toContain(`Limen ${limen}`);
    expect(visible).not.toContain("Echelon runtime registry");
  }
});

test("UI-018-O6", async ({ page, causa }) => {
  await causa.open("#/administration");
  await expect(fact(page, "limen")).toHaveAttribute("data-state", "verified");
  // Read-only: no operational action is offered.
  await expect(admin(page).locator("button, input, select, textarea, form, [data-event]")).toHaveCount(0);
});
