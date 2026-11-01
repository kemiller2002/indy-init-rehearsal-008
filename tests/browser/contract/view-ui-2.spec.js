// Owner: ui-2 (WI-R8-T23). IF41-VIEW contract (ui-2): the causal graph projection (Views/Graph.fs) reaches the page
// through Limen and is laid out by the existing Echelon workflow engine (@echelon-foundry/forma-workflow, WDL-001).
//   - the graph's nodes and relationships come only from events the domain kernel accepted (ADRP-007-O1, UI-002-O5);
//   - the projected Forma document authors no coordinates, and every position on the page comes from the engine
//     (WDL-002, WDL-005);
//   - the same history gives the same document and the same placement (UI-002-O4, WDL-009);
//   - the same nodes and relationships are listed as text (ADRP-007-O1, UI-002-O4).
//
// WI-R8-T28 (S14, S15; DEC-R7-005): the history region (Views/History.fs) is the kernel's history replayed to a chosen
// version. The timeline is in canonical version order; the scrubber shows state-at-version and a semantic diff to the
// latest version in the kernel's terms; a historical version is visibly read-only and never changes the investigation.
import { expect, test } from "../support/fixtures.js";

const submit = (page, name) => page.locator("[data-causa-workspace]").getByRole("button", { name, exact: true }).click();
const graph = (page) => page.locator("[data-causa-region='graph']");
const engineNodes = (page) => page.locator("forma-workflow[data-causa-graph] .ef-diagram-node");
const documentOf = async (page) => JSON.parse(await page.locator("forma-workflow[data-causa-graph]").getAttribute("data-workflow"));
const placement = (page) =>
  engineNodes(page).evaluateAll((nodes) =>
    nodes.map((n) => ["x", "y", "w", "h"].map((p) => n.style.getPropertyValue(`--ef-diagram-${p}`).trim()).join(","))
  );

// Start, record two observations, activate, record a conclusion: the kernel accepts all five.
const buildInvestigation = async (page, causa) => {
  await causa.open("#/investigations/new");
  for (const button of ["Start investigation", "Record an observation", "Record an observation", "Activate", "Record a conclusion"]) {
    await submit(page, button);
    await expect(page.locator("[data-causa-outcome]")).toHaveAttribute("data-outcome", "accepted");
  }
};

test("renders the projection", async ({ page, causa, failures }) => {
  await causa.open("#/investigations/new");
  // No investigation: no graph, and the page says so.
  await expect(graph(page).locator("[data-causa-graph-summary]")).toHaveText("No graph yet: start an investigation to see its entries.");
  await expect(graph(page).locator("figure.causa-graph-figure")).toBeHidden();
  await expect(graph(page).locator("[data-causa-graph-relationships] li")).toHaveCount(0);

  await buildInvestigation(page, causa);

  // The projection: the investigation and the three recorded entries, in canonical version order, and one
  // "records" relationship per entry. Nothing else (no inferred causal edge).
  const projected = await documentOf(page);
  expect(projected.format).toBe("forma-workflow");
  expect(projected.nodes.map((n) => n.label)).toEqual([projected.title, "Observation 1", "Observation 2", "Conclusion 1"]);
  expect(projected.nodes.map((n) => n.kindLabel ?? n.kind)).toEqual(["start", "Observation", "Observation", "Conclusion"]);
  expect(projected.edges).toHaveLength(3);
  for (const edge of projected.edges) {
    expect(edge.source.node).toBe(projected.nodes[0].id);
    expect(edge.label).toBe("records");
  }
  expect(projected.nodes[0].status).toEqual({ state: "active", label: "Active" });
  // Stable identities from the kernel's event ids, never array positions.
  for (const node of projected.nodes.slice(1)) expect(node.id).toMatch(/^ev-[0-9a-f-]{36}$/);

  // No authored geometry: the engine places every node (WDL-002, WDL-005).
  expect(projected.layout).toBeUndefined();
  for (const node of projected.nodes) expect(node.layout ?? node.position ?? node.x, `${node.id} authors no position`).toBeUndefined();
  await expect(engineNodes(page)).toHaveCount(4, { timeout: 30_000 });
  const first = await placement(page);
  for (const box of first) expect(box, "the engine placed the node").not.toMatch(/^,|,,|,$/);
  expect(new Set(first).size).toBe(4);

  // Textual alternative: the same relationships and entries, with provenance.
  const relationships = graph(page).locator("[data-causa-graph-relationships] li");
  await expect(relationships).toHaveText([
    `${projected.title} records Observation 1`,
    `${projected.title} records Observation 2`,
    `${projected.title} records Conclusion 1`
  ]);
  await expect(graph(page).locator("[data-causa-graph-nodes] li")).toHaveCount(4);
  await expect(graph(page).locator("[data-causa-graph-nodes] li").nth(1)).toContainText("ObservationRecorded, event");
  await expect(graph(page).locator("[data-causa-graph-summary]")).toHaveText("4 nodes and 3 relationships, laid out by the Forma workflow engine.");
  // Graph and every other region read the same projection version (ADRP-004).
  await expect(graph(page)).toHaveAttribute("data-projection-version", "5");

  // A refused command changes nothing in the graph.
  await submit(page, "Start investigation");
  await expect(page.locator("[data-causa-outcome]")).toHaveAttribute("data-outcome", "refused");
  expect(await documentOf(page)).toEqual(projected);

  // Deterministic: a fresh tab with the same history gives the same document and the same placement.
  await page.reload();
  await buildInvestigation(page, causa);
  expect(await documentOf(page)).toEqual(projected);
  await expect(engineNodes(page)).toHaveCount(4, { timeout: 30_000 });
  await expect.poll(() => placement(page)).toEqual(first);

  expect(failures).toEqual([]);
});

const history = (page) => page.locator("[data-causa-region='history']");
const scrubTo = (page, version) =>
  page.locator("[data-causa-history-scrubber]").evaluate((input, v) => {
    input.value = String(v);
    input.dispatchEvent(new Event("change", { bubbles: true }));
  }, version);

test("time travel and semantic diff", async ({ page, causa, failures }) => {
  await causa.open("#/investigations/new");
  // No investigation: no history, and the page says so.
  await expect(history(page).locator("[data-causa-history-empty]")).toBeVisible();
  await expect(history(page).locator("[data-causa-history-body]")).toBeHidden();

  await buildInvestigation(page, causa);

  // The aggregate-version timeline with event summaries, in canonical version order (ADRP-007-O2).
  const timeline = history(page).locator("[data-causa-history-timeline] li");
  await expect(timeline).toHaveText([
    "Version 1: Investigation started (InvestigationStarted).",
    "Version 2: Observation recorded (ObservationRecorded).",
    "Version 3: Observation recorded (ObservationRecorded).",
    "Version 4: Investigation activated (InvestigationActivated).",
    "Version 5: Conclusion recorded (ConclusionRecorded)."
  ]);
  await expect(history(page)).toHaveAttribute("data-history-mode", "latest");
  await expect(history(page).locator("[data-causa-history-at]")).toHaveText("5");
  await expect(history(page).locator("[data-causa-history-readonly]")).toBeHidden();
  await expect(timeline.nth(4)).toHaveAttribute("aria-current", "true");
  await expect(history(page).locator("[data-causa-history-diff] li")).toHaveCount(0);

  // Scrub to version 2: the state the kernel replays at version 2, visibly read-only.
  await scrubTo(page, 2);
  await expect(history(page)).toHaveAttribute("data-history-mode", "historical");
  await expect(history(page).locator("[data-causa-history-readonly]")).toHaveText(
    "Viewing version 2 of 5. Historical view: read-only; the investigation is unchanged."
  );
  await expect(history(page).locator("[data-causa-history-lifecycle]")).toHaveText("Draft");
  await expect(timeline.nth(1)).toHaveAttribute("aria-current", "true");
  await expect(timeline.nth(4)).toHaveAttribute("aria-current", "false");

  // The semantic diff from version 2 to the latest, in the kernel's terms; nothing inferred.
  const diff = history(page).locator("[data-causa-history-diff] li");
  await expect(diff.filter({ hasText: "Lifecycle: Draft to Active." })).toHaveCount(1);
  await expect(diff.filter({ hasText: "Added Observation 2 (ObservationRecorded, version 3)." })).toHaveCount(1);
  await expect(diff.filter({ hasText: "Added Conclusion 1 (ConclusionRecorded, version 5)." })).toHaveCount(1);
  await expect(diff.filter({ hasText: "Added Observation 1" })).toHaveCount(0);
  await expect(history(page).locator("[data-causa-history-diff-summary]")).toHaveText(/^From version 2 to version 5: \d+ changes\.$/);
  await expect(history(page).locator("[data-causa-history-policy]")).toContainText("No policy or domain-version change");

  // Time travel never changes the live investigation: the workspace still reads version 5, Active.
  await expect(page.locator("[data-causa-version]")).toHaveText("5");
  await expect(page.locator("[data-causa-lifecycle]")).toHaveText("Active");
  await expect(graph(page)).toHaveAttribute("data-projection-version", "5");

  // Return to the latest version.
  await history(page).getByRole("button", { name: "Return to the latest version" }).click();
  await expect(history(page)).toHaveAttribute("data-history-mode", "latest");
  await expect(history(page).locator("[data-causa-history-readonly]")).toBeHidden();
  await expect(history(page).locator("[data-causa-history-lifecycle]")).toHaveText("Active");

  expect(failures).toEqual([]);
});
