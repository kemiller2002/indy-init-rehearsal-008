// WDL-002 — no duplicate workflow layout engine (WI-R8-T24, DEC-030).
//
// O1: Causa's own source carries no second general-purpose graph layout implementation and no fixed diagram
//     coordinates for domain nodes and edges; on the working example, every coordinate on the page comes from the
//     existing engine (@echelon-foundry/forma-workflow), not from the authored document.
// O2: a temporary layout fallback is only allowed with a recorded approval, and is never counted as satisfying WDL-002.
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { join, relative } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, test } from "@playwright/test";

const root = fileURLToPath(new URL("../../../", import.meta.url));
const fixture = "/tests/browser/support/wdl-001-fixture.html";

// Causa's application source: what the application ships (not tests, not vendored packages).
const sourceRoots = ["src", "web"].map((d) => join(root, d)).filter(existsSync);
const sourceFile = /\.(fs|fsx|js|mjs|ts|html|css)$/;
const skipDir = new Set(["bin", "obj", "node_modules", "wwwroot"]);

const walk = (dir) =>
  readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    if (statSync(path).isDirectory()) return skipDir.has(name) ? [] : walk(path);
    return sourceFile.test(name) ? [path] : [];
  });

const sources = () => sourceRoots.flatMap(walk).map((path) => ({ path: relative(root, path), text: readFileSync(path, "utf8") }));

// Known general-purpose graph layout libraries and algorithms, and the engine's own geometry properties written by hand.
const layoutLibrary = /\b(dagre|elkjs|cytoscape|d3-dag|graphlib|sugiyama|cola\.js|webcola|klay)\b/i;
const authoredGeometry = /--ef-diagram-[xywh]\s*:/;
const fallbackMarker = /data-layout-fallback|layout-fallback/;
const approvals = join(root, "evidence", "WDL-002-fallback-approval.json");

test("WDL-002-O1", async ({ page }) => {
  const findings = sources().flatMap(({ path, text }) => [
    ...(layoutLibrary.test(text) ? [`${path}: references a graph layout library or algorithm`] : []),
    ...(authoredGeometry.test(text) ? [`${path}: authors diagram node coordinates`] : [])
  ]);
  expect(findings).toEqual([]);

  // The working example authors no geometry; the engine supplies every position.
  await page.goto(fixture);
  const authored = JSON.parse(await page.locator("script.forma-workflow-source").textContent());
  for (const node of authored.nodes) expect(node.layout ?? node.position ?? node.x, `${node.id} authors no position`).toBeUndefined();
  expect(authored.layout).toBeUndefined();

  const nodes = page.locator("forma-workflow .ef-diagram-node");
  await expect(nodes).toHaveCount(authored.nodes.length, { timeout: 30_000 });
  const placed = await nodes.evaluateAll((els) => els.map((n) => n.style.getPropertyValue("--ef-diagram-x").trim()));
  for (const x of placed) expect(x, "the engine placed the node").not.toBe("");
  expect(new Set(placed).size).toBeGreaterThan(1);
});

test("WDL-002-O2", async () => {
  const fallbacks = sources().filter(({ text }) => fallbackMarker.test(text)).map(({ path }) => path);
  if (fallbacks.length === 0) {
    expect(fallbacks).toEqual([]);
    return;
  }
  // A fallback exists: it needs a recorded approval naming every file, and the approval cannot claim WDL-002.
  expect(existsSync(approvals), "a layout fallback needs evidence/WDL-002-fallback-approval.json").toBe(true);
  const approval = JSON.parse(readFileSync(approvals, "utf8"));
  expect(approval.approvedBy, "approval names who approved it").toBeTruthy();
  expect(approval.satisfies ?? [], "a fallback never satisfies WDL-002").not.toContain("WDL-002");
  for (const path of fallbacks) expect(approval.files ?? [], `${path} is approved`).toContain(path);
});
