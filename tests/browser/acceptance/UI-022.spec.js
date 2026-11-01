// Owner: ui (WI-R8-UIP0). UI-022: minimal application chrome. O3-O6 (catalog cards) need a connected catalog and are
// not specified until Connect is wired.
import { expect, test } from "../support/fixtures.js";

const routes = ["#/", "#/investigations/new", "#/investigations/0f8fad5b-d9cb-469f-a165-70867728950e?repo=kemiller2002/causa-data", "#/administration", "#/settings"];

test("UI-022-O1", async ({ page, causa }) => {
  for (const route of routes) {
    await causa.open(route);
    const header = page.locator(".causa-masthead");
    await expect(header.locator(".ef-site-header__name")).toHaveText("Causa");
    for (const name of ["Investigations", "Administration", "Settings"]) {
      await expect(page.getByRole("navigation", { name: "Primary" }).getByRole("link", { name, exact: true })).toBeAttached();
    }
    const visible = await header.innerText();
    // No data-provider commit hash, no connection implementation (API host, token, branch or ref) in the header.
    expect(visible, route).not.toMatch(/\b[0-9a-f]{7,40}\b|api\.github\.com|token|refs\/|branch|HEAD/i);
  }
});

test("UI-022-O2", async ({ page, causa }) => {
  for (const route of routes.filter((r) => r !== "#/administration")) {
    await causa.open(route);
    const footer = await page.locator("footer, [role=contentinfo]").allInnerTexts();
    for (const text of footer) {
      expect(text, route).not.toMatch(/\b[0-9a-f]{7,40}\b|\d{4}-\d{2}-\d{2}T\d{2}:\d{2}|\bbuilt\b|\bbuild\b|@echelon-foundry|\bv?\d+\.\d+\.\d+\b/i);
    }
  }
});
