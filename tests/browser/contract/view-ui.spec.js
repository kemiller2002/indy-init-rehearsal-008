// Owner: ui (T16). IF41-VIEW contract (ui): the shell projection reaches the page through Limen bindings, complete
// on every route, with canonical hash URLs (R9.6, R9.7) and the not-found state for unknown or refused URLs (R9.8,
// R9.9).
import { expect, test } from "../support/fixtures.js";

const id = "0f8fad5b-d9cb-469f-a165-70867728950e";

test("renders the projection", async ({ page, causa, failures }) => {
  await causa.open(`#/investigations/${id}?tab=hypotheses&repo=kemiller2002/causa-data&ignored=1`);
  await expect(page.locator("[data-causa-screen]")).toHaveText("investigation");
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Hypotheses");
  const nav = page.getByRole("navigation", { name: "Investigation" });
  // Canonical form: declared parameters only, in contract order, defaults omitted.
  await expect(nav.getByRole("link", { name: "Overview" })).toHaveAttribute("href", `#/investigations/${id}?repo=kemiller2002/causa-data`);
  await expect(nav.getByRole("link", { name: "Hypotheses" })).toHaveAttribute("href", `#/investigations/${id}?repo=kemiller2002/causa-data&tab=hypotheses`);
  await expect(nav.getByRole("link", { name: "Hypotheses" })).toHaveAttribute("aria-current", "page");

  // A reload of a deep link reproduces the same view (hash routes never reach the server).
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-engine", "ready", { timeout: 30_000 });
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Hypotheses");

  // Unknown targets, non-canonical identifiers and reserved parameter names show not found with a way home.
  for (const hash of ["#/nope", `#/investigations/${id.toUpperCase()}`, "#/investigations/not-a-guid", `#/investigations/${id}?token=x`, "#/settings?code=abc"]) {
    await causa.open(hash);
    await expect(page.locator("[data-causa-screen]"), hash).toHaveText("not-found");
    await expect(page.getByRole("heading", { level: 1 })).toHaveText("Page not found");
    await expect(page.getByRole("link", { name: "Go to Investigations" })).toHaveAttribute("href", "#/");
    await expect(page.getByRole("navigation", { name: "Investigation" })).toBeHidden();
  }
  // Every destination projects; engineReady stays true throughout.
  for (const [hash, screen] of [["#/", "home"], ["#/investigations/new", "investigation-start"], ["#/administration", "administration"], ["#/settings", "settings"]]) {
    await causa.open(hash);
    await expect(page.locator("[data-causa-screen]")).toHaveText(screen);
    await expect(page.locator("html")).toHaveAttribute("data-engine", "ready");
  }

  // WI-R8-T22: the investigation workspace views read one projection of one history (ADRP-004): every region carries
  // the same projection version, and observations, hypotheses, conclusions, blockers and the inspector follow it.
  await causa.open("#/investigations/new");
  const workspace = page.locator("[data-causa-workspace]");
  const submit = (name) => workspace.getByRole("button", { name, exact: true }).click();
  await expect(workspace.locator("[data-causa-evidence-list]")).toContainText("No evidence recorded yet.");
  await expect(workspace.locator('[data-causa-region="inspector"]')).toBeHidden();
  await submit("Start investigation");
  await submit("Record an observation");
  await submit("Activate");
  await submit("Record a hypothesis");
  await expect(workspace.locator("[data-causa-version]")).toHaveText("4");
  await expect(workspace.locator("[data-causa-observation-list]")).toHaveText("Observation 1 (version 2)");
  await expect(workspace.locator("[data-causa-hypothesis-list]")).toHaveText("Hypothesis 1 (version 4)");
  await expect(workspace.locator("[data-causa-conclusion-list]")).toHaveText("No conclusion recorded yet.");
  await expect(workspace.locator("[data-causa-inspector-kind]")).toHaveText("HypothesisRecorded");
  await expect(workspace.locator("[data-causa-last-change]")).toHaveText("HypothesisRecorded at version 4");
  await expect(workspace.locator("[data-causa-timeline-list]")).toContainText("1. InvestigationStarted; 2. ObservationRecorded");
  // Unavailable actions stay discoverable with the kernel's reason (INVESTIGATION-WORKSPACE section 19).
  await expect(workspace.locator('[data-causa-capability="StartInvestigation"]')).toContainText("Unavailable:");
  await expect(workspace.locator("[data-causa-next-actions]")).not.toHaveText("");
  const versions = await workspace.locator("[data-causa-region]").evaluateAll((els) => els.map((el) => el.getAttribute("data-projection-version")));
  expect(versions.length).toBeGreaterThanOrEqual(8);
  expect(new Set(versions)).toEqual(new Set(["4"]));
  expect(failures).toEqual([]);
});
