// WDL-001 — existing workflow layout capability discovery (WI-R8-T24, DEC-030).
//
// O1: the discovery record (evidence/T24-layout-engine.md) names the owner repository, package, version and
//     license, and those facts match the package actually installed for the application.
// O2: a working example: the existing engine (@echelon-foundry/forma-workflow, F# Forma.Workflow on .NET
//     WebAssembly) lays out a Causa investigation workflow that authors no coordinates, validly and deterministically.
//
// Self-contained: the fixture page lives at tests/browser/support/wdl-001-fixture.html and is served by the
// repository-root web server in playwright.config.js. Needs CR-T24-01 (package.json dependency) to be applied.
import { readFileSync } from "node:fs";
import { expect, test } from "@playwright/test";

const root = new URL("../../../", import.meta.url);
const readText = (path) => readFileSync(new URL(path, root), "utf8");
const fixture = "/tests/browser/support/wdl-001-fixture.html";
const packageName = "@echelon-foundry/forma-workflow";

// Records every forma-workflow-* event the page raises, before any page script runs.
const recordEngineEvents = (page) =>
  page.addInitScript(() => {
    window.__wdlEvents = [];
    for (const type of ["forma-workflow-ready", "forma-workflow-load", "forma-workflow-error"])
      document.addEventListener(type, (e) => window.__wdlEvents.push({ type, detail: e.detail ?? null }));
  });

// The geometry the engine produced: one entry per node and per connector, in document order.
const engineGeometry = (page) =>
  page.locator("forma-workflow").evaluate((host) => ({
    nodes: [...host.querySelectorAll(".ef-diagram-node")].map((n) => {
      const r = n.getBoundingClientRect();
      return { id: n.id, label: n.querySelector(".ef-diagram-node__label")?.textContent ?? "", x: r.x, y: r.y, w: r.width, h: r.height };
    }),
    connectors: [...host.querySelectorAll(".ef-diagram__wires .ef-diagram-connector")].map((p) => ({ id: p.id, d: p.getAttribute("d") }))
  }));

const renderExample = async (page) => {
  await recordEngineEvents(page);
  await page.goto(fixture);
  await expect(page.locator("forma-workflow .ef-diagram-node")).toHaveCount(5, { timeout: 30_000 });
  return engineGeometry(page);
};

const overlaps = (a, b) => a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h;

test("WDL-001-O1", async () => {
  const record = readText("evidence/T24-layout-engine.md");
  const installed = JSON.parse(readText(`node_modules/${packageName}/package.json`));

  expect(installed.name).toBe(packageName);
  expect(installed.repository.url).toContain("github.com/kemiller2002/forma");
  expect(record).toContain("`kemiller2002/forma`");
  expect(record).toContain(`\`${installed.name}\``);
  expect(record).toContain(`\`${installed.version}\``);
  expect(record).toContain(`\`${installed.license}\``);
  for (const field of ["Owner repository", "Package", "Version", "License", "Public API", "Constraints"])
    expect(record, `record names ${field}`).toContain(field);
  // The engine composes with the locked design-system package rather than replacing it.
  expect(installed.peerDependencies["@echelon-foundry/design-system"]).toBe(">=0.4.0");
});

test("WDL-001-O2", async ({ page, browser }) => {
  const failures = [];
  page.on("pageerror", (error) => failures.push(`script error: ${error.message}`));
  page.on("response", (r) => {
    if (r.status() >= 400 && !r.url().endsWith("/favicon.ico")) failures.push(`${r.status()} ${r.url()}`);
  });

  const first = await renderExample(page);

  // The engine started and accepted the document as valid.
  const events = await page.evaluate(() => window.__wdlEvents);
  expect(events.map((e) => e.type)).not.toContain("forma-workflow-error");
  const load = events.find((e) => e.type === "forma-workflow-load");
  expect(load, "forma-workflow-load was raised").toBeTruthy();
  expect(load.detail.valid).toBe(true);

  // Every node is laid out with real size, none overlap, and every edge (including the loop back) is routed.
  expect(first.nodes.map((n) => n.label)).toEqual([
    "Observe incident", "Form hypothesis", "Collect evidence", "Evidence supports it?", "Confirm root cause"
  ]);
  for (const n of first.nodes) expect(n.w > 0 && n.h > 0, `${n.id} has size`).toBe(true);
  for (const [i, a] of first.nodes.entries())
    for (const b of first.nodes.slice(i + 1)) expect(overlaps(a, b), `${a.id} overlaps ${b.id}`).toBe(false);
  expect(first.connectors).toHaveLength(5);
  for (const c of first.connectors) expect(c.d, `${c.id} is routed`).toMatch(/^M[\d.]+ [\d.]+( L[\d.]+ [\d.]+)+$/);

  // The diagram is announced and has an accessible name.
  await expect(page.locator("forma-workflow figure.ef-diagram")).toHaveAccessibleName("Root cause investigation");

  // Deterministic: a fresh page given the same document gets the same layout.
  const context = await browser.newContext({ viewport: page.viewportSize() });
  const second = await renderExample(await context.newPage());
  await context.close();
  expect(second).toEqual(first);

  expect(failures).toEqual([]);
});
