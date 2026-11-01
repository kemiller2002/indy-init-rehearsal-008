// Owner: ui-2 (WI-R8-T23). IF41-VIEW contract (ui-2): the causal graph projection (Views/Graph.fs) reaches the page
// through Limen and is laid out by the existing Echelon workflow engine (@echelon-foundry/forma-workflow, WDL-001).
//   - the graph's nodes and relationships come only from events the domain kernel accepted (ADRP-007-O1, UI-002-O5);
//   - the projected Forma document authors no coordinates, and every position on the page comes from the engine
//     (WDL-002, WDL-005);
//   - the same history gives the same document and the same placement (UI-002-O4, WDL-009);
//   - the same nodes and relationships are listed as text (ADRP-007-O1, UI-002-O4).
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
