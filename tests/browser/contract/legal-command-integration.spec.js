// Owner: integration (WI-R8-T17, integration checkpoint 1; ADRP-012 "end-to-end legal action", ADRP-004).
// On the deployable site (dist/, as published), a click sends a typed command through Limen into the F# engine; the
// domain kernel decides it. Allowed: events are stamped and evolved and every dependent region refreshes from one
// projection version. Refused: the kernel's typed answer and reason reach the page and nothing else changes.
import { expect, test } from "../support/fixtures.js";

const regions = (page) => page.locator("[data-causa-workspace] [data-causa-region]");

/** Every dependent region carries the same projection version, and it is the version the page shows. */
const expectOneProjection = async (page, version) => {
  const workspace = page.locator("[data-causa-workspace]");
  await expect(workspace.locator("[data-causa-version]")).toHaveText(String(version));
  const versions = await regions(page).evaluateAll((els) => els.map((el) => el.getAttribute("data-projection-version")));
  expect(versions.length).toBeGreaterThanOrEqual(3);
  expect(new Set(versions)).toEqual(new Set([String(version)]));
};

const submit = (page, name) => page.locator("[data-causa-workspace]").getByRole("button", { name, exact: true }).click();

test("a legal command crosses Limen, the kernel decides it, and every view refreshes from one projection", async ({ page, causa, failures }) => {
  // Record every message the page sends to the engine (Limen's transport) without changing it.
  await page.addInitScript(() => {
    globalThis.__causaSent = [];
    const stringify = JSON.stringify;
    JSON.stringify = function (value, ...rest) {
      if (value && typeof value === "object" && value.kind === "Event") globalThis.__causaSent.push(value.event?.name);
      return stringify.call(this, value, ...rest);
    };
  });
  await causa.open("#/investigations/new");
  const workspace = page.locator("[data-causa-workspace]");
  await expect(workspace).toBeVisible();
  await expect(workspace).toContainText("Not saved");
  await expect(workspace.locator("[data-causa-lifecycle]")).toHaveText("No investigation");
  await expectOneProjection(page, 0);
  await expect(workspace.locator('[data-causa-capability="StartInvestigation"]')).toHaveText("Available");

  // Allowed: StartInvestigation -> InvestigationStarted at version 1, a Draft investigation.
  await submit(page, "Start investigation");
  await expect(workspace.locator("[data-causa-outcome]")).toHaveText("Start investigation: done.");
  await expect(workspace.locator("[data-causa-outcome]")).toHaveAttribute("data-outcome", "accepted");
  await expect(workspace.locator("[data-causa-lifecycle]")).toHaveText("Draft");
  await expect(workspace.locator("[data-causa-timeline-count]")).toHaveText("1");
  await expect(workspace.locator("[data-causa-timeline-latest]")).toHaveText("InvestigationStarted");
  await expectOneProjection(page, 1);
  // The capabilities are the kernel's, with its reasons: a second start is illegal; activation needs an observation.
  await expect(workspace.locator('[data-causa-capability="StartInvestigation"]')).toContainText("Unavailable:");
  await expect(workspace.locator('[data-causa-capability="ActivateInvestigation"]')).toContainText("Record at least one observation before activating");

  const sent = await page.evaluate(() => globalThis.__causaSent);
  expect(sent).toContain("submit.StartInvestigation");
  expect(failures).toEqual([]);
});

test("a refused command shows the kernel's reason and changes nothing", async ({ page, causa, failures }) => {
  await causa.open("#/investigations/new");
  const workspace = page.locator("[data-causa-workspace]");
  await submit(page, "Start investigation");
  await expectOneProjection(page, 1);

  // Blocked by policy: activation before any observation.
  await submit(page, "Activate");
  const outcome = workspace.locator("[data-causa-outcome]");
  await expect(outcome).toHaveAttribute("data-outcome", "refused");
  await expect(outcome).toContainText("Activate was not done:");
  await expect(outcome).toHaveAttribute("data-blocker-codes", "activation-policy");
  await expect(workspace.locator("[data-causa-lifecycle]")).toHaveText("Draft");
  await expect(workspace.locator("[data-causa-timeline-count]")).toHaveText("1");
  await expectOneProjection(page, 1);

  // Illegal transition: starting an investigation that already exists.
  await submit(page, "Start investigation");
  await expect(outcome).toContainText("Start investigation was not done: This investigation has already been started.");
  await expect(outcome).toHaveAttribute("data-blocker-codes", "illegal-transition");
  await expectOneProjection(page, 1);
  expect(failures).toEqual([]);
});

test("a sequence of legal commands walks the lifecycle with one projection per answer", async ({ page, causa, failures }) => {
  await causa.open("#/investigations/new");
  const workspace = page.locator("[data-causa-workspace]");
  const steps = [
    ["Start investigation", "InvestigationStarted", "Draft", 1],
    ["Record an observation", "ObservationRecorded", "Draft", 2],
    ["Activate", "InvestigationActivated", "Active", 3]
  ];
  for (const [button, event, lifecycle, version] of steps) {
    await submit(page, button);
    await expect(workspace.locator("[data-causa-outcome]")).toHaveAttribute("data-outcome", "accepted");
    await expect(workspace.locator("[data-causa-timeline-latest]")).toHaveText(event);
    await expect(workspace.locator("[data-causa-lifecycle]")).toHaveText(lifecycle);
    await expectOneProjection(page, version);
  }
  // Resolution needs a conclusion first (the kernel's resolution gate), then it is allowed.
  await expect(workspace.locator('[data-causa-capability="ResolveInvestigation"]')).toContainText("Record a conclusion before resolving");
  await submit(page, "Resolve");
  await expect(workspace.locator("[data-causa-outcome]")).toHaveAttribute("data-blocker-codes", "resolution-gate");
  await expectOneProjection(page, 3);
  await submit(page, "Record a conclusion");
  await expectOneProjection(page, 4);
  await expect(workspace.locator('[data-causa-capability="ResolveInvestigation"]')).toHaveText("Available");
  await submit(page, "Resolve");
  await expect(workspace.locator("[data-causa-lifecycle]")).toHaveText("Resolved");
  await expectOneProjection(page, 5);
  expect(failures).toEqual([]);
});

test("each tab holds its own workspace and a reload starts empty (nothing is claimed saved)", async ({ page, causa }) => {
  await causa.open("#/investigations/new");
  await submit(page, "Start investigation");
  await expectOneProjection(page, 1);
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-engine", "ready", { timeout: 30_000 });
  await expect(page.locator("[data-causa-workspace] [data-causa-lifecycle]")).toHaveText("No investigation");
  await expectOneProjection(page, 0);
});
