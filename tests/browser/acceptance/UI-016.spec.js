// Owner: ui (T16). UI-016: the Causa semantic colour system, on the application shell and the loading shell.
// Colours come only from Forma 0.12.0 semantic tokens (FL-06, FL-10); amber is Forma's warning status colour
// (ODR-V41-05) and always carries a text label (DEC-R7-002).
import { expect, test } from "../support/fixtures.js";
import { amberLabelled, onlyFormaTokens, textContrast } from "../support/layout-assertions.mjs";

const investigation = "#/investigations/0f8fad5b-d9cb-469f-a165-70867728950e?repo=kemiller2002/causa-data";
const formaCss = "/node_modules/@echelon-foundry/design-system/dist/all.css";

// The colour of a CSS colour string as [r, g, b], resolved by the browser.
const rgbOf = (page, selector, property) =>
  page.locator(selector).first().evaluate((e, p) => {
    const c = getComputedStyle(e)[p];
    const ctx = Object.assign(document.createElement("canvas"), { width: 1, height: 1 }).getContext("2d");
    ctx.fillStyle = c;
    ctx.fillRect(0, 0, 1, 1);
    return [...ctx.getImageData(0, 0, 1, 1).data.slice(0, 3)];
  }, property);
const chroma = ([r, g, b]) => Math.max(r, g, b) - Math.min(r, g, b);
const tokenOf = (page, name) =>
  page.evaluate((n) => getComputedStyle(document.documentElement).getPropertyValue(n).trim(), name);

test("UI-016-O1", async ({ page, causa }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await causa.open(investigation);
  // Visibly coloured, not monochrome: the workspace surface (ivory role) and the active destination (teal role) carry
  // hue; the masthead is the structural surface.
  const surface = await rgbOf(page, "body", "backgroundColor");
  const active = await rgbOf(page, '.causa-investigation-nav a[aria-current="page"]', "color");
  expect(chroma(surface)).toBeGreaterThan(5);
  expect(chroma(active)).toBeGreaterThan(30);
  const masthead = await rgbOf(page, ".causa-masthead", "backgroundColor");
  expect(masthead).not.toEqual(surface);
});

test("UI-016-O8", async ({ page, causa }) => {
  await causa.open(investigation);
  const css = await (await page.request.get(formaCss)).text();
  const inventory = [...new Set([...css.matchAll(/(--ef-[a-z0-9-]+)\s*:/g)].map((m) => m[1]))];
  // Forma's hooks: properties Forma's own stylesheets (the design system and its workflow engine, which lays out the
  // causal graph, WI-R8-T23) read but never define. The engine sets them on the diagrams it lays out.
  const workflowCss = await (await page.request.get("/dist/node_modules/@echelon-foundry/forma-workflow/dist/forma-workflow.css")).text();
  const hooks = [...new Set([...`${css}\n${workflowCss}`.matchAll(/var\(\s*(--ef-[a-z0-9-]+)/g)].map((m) => m[1]))].filter((p) => !inventory.includes(p));
  expect(await onlyFormaTokens(page, { inventory, hooks, formaHref: "/design-system/dist/all.css" })).toEqual([]);
  // The shell's colours resolve to Forma's semantic tokens, not literals.
  expect(await rgbOf(page, ".causa-masthead", "backgroundColor")).toEqual(
    await page.evaluate((t) => { const d = document.createElement("div"); d.style.color = `var(${t})`; document.body.append(d); const c = getComputedStyle(d).color; d.remove(); return c; }, "--ef-color-surface-inverse").then(async (c) => {
      return page.evaluate((col) => { const ctx = Object.assign(document.createElement("canvas"), { width: 1, height: 1 }).getContext("2d"); ctx.fillStyle = col; ctx.fillRect(0, 0, 1, 1); return [...ctx.getImageData(0, 0, 1, 1).data.slice(0, 3)]; }, c);
    })
  );
  const causaCss = await (await page.request.get("/dist/causa.css")).text();
  expect(causaCss).not.toMatch(/#[0-9a-fA-F]{3,8}\b|rgb\(|hsl\(|oklch\(/);
  expect(causaCss).not.toMatch(/--ef-[a-z0-9-]+\s*:/);
});

test("UI-016-O9", async ({ page, causa }) => {
  await causa.open(investigation);
  for (const selector of [".ef-skip-link", ".ef-site-header", ".ef-navigation-shell", ".ef-status-lozenge"]) {
    await expect(page.locator(selector).first()).toBeAttached();
  }
  // Focus treatment is Forma's: the focus ring colour is --ef-color-focus-ring.
  await page.keyboard.press("Tab");
  const outline = await page.evaluate(() => getComputedStyle(document.activeElement).outlineStyle);
  expect(outline).not.toBe("none");
  expect(await tokenOf(page, "--ef-color-focus-ring")).not.toBe("");
});

test("UI-016-O11", async ({ page, causa }) => {
  for (const viewport of [{ width: 320, height: 640 }, { width: 1440, height: 900 }]) {
    for (const colorScheme of ["light", "dark"]) {
      await page.setViewportSize(viewport);
      await page.emulateMedia({ colorScheme });
      for (const hash of ["#/", investigation]) {
        await causa.open(hash);
        expect(await textContrast(page), `${hash} ${colorScheme} at ${viewport.width}`).toEqual([]);
      }
    }
  }
});

test("UI-016-O12", async ({ page, causa }) => {
  await causa.open(investigation);
  expect(await amberLabelled(page)).toEqual([]);
});

test("UI-016-O13", async ({ page, causa }) => {
  await causa.open(investigation);
  // Sync status: text, not colour alone.
  await expect(page.locator(".causa-sync")).toContainText("Sync:");
  await expect(page.locator(".causa-sync .ef-status-lozenge")).toHaveText("Not connected");
  // Current navigation: aria-current plus a shape cue (a border) beside the colour.
  const border = await page
    .locator('.causa-investigation-nav a[aria-current="page"]')
    .evaluate((a) => getComputedStyle(a).borderBlockEndStyle);
  expect(border).toBe("solid");
});
