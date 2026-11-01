// Owner: ui (WI-R8-UIP0, WI-R8-UTESTS). INV-10: two tabs are independent; a change in one never appears in another
// until it is saved to GitHub and the other loads it from GitHub; correctness never depends on cross-tab coordination,
// so the tabs behave the same whether or not the browser offers navigator.locks.
import { expect, sitePath, test } from "../support/fixtures.js";

const ready = (page) => expect(page.locator("html")).toHaveAttribute("data-engine", "ready", { timeout: 30_000 });
const ws = (page) => page.locator("[data-causa-workspace]");
const press = (page, name) => ws(page).getByRole("button", { name, exact: true }).click();
const version = (page) => page.locator('[data-text="projectionVersion"]');

/** Opens two tabs on the workspace in one browser context, optionally with navigator.locks removed before any script. */
const twoTabs = async (browser, withoutLocks) => {
  const context = await browser.newContext();
  if (withoutLocks) {
    await context.addInitScript(() => {
      Object.defineProperty(Navigator.prototype, "locks", { configurable: true, get: () => undefined });
    });
  }
  const pages = [await context.newPage(), await context.newPage()];
  for (const p of pages) {
    await p.goto(`${sitePath}#/investigations/new`);
    await ready(p);
    expect(await p.evaluate(() => "locks" in navigator && navigator.locks !== undefined)).toBe(!withoutLocks);
  }
  return { context, pages };
};

const independent = async ({ pages: [a, b] }) => {
  // Interleaved edits in both tabs: each tab's projection reflects only its own commands.
  await press(a, "Start investigation");
  await press(b, "Start investigation");
  await press(a, "Record an observation");
  await expect(version(a)).toHaveText("2");
  await expect(version(b)).toHaveText("1");
  await press(b, "Record an observation");
  await press(b, "Activate");
  await press(a, "Record an observation");
  await expect(version(a)).toHaveText("3");
  await expect(version(b)).toHaveText("3");
  await expect(a.locator('[data-text="observationCount"]')).toHaveText("2");
  await expect(b.locator('[data-text="observationCount"]')).toHaveText("1");
  // A third tab opened now starts with nothing; neither tab's unsaved change reached it.
  const c = await a.context().newPage();
  await c.goto(`${sitePath}#/investigations/new`);
  await ready(c);
  await expect(version(c)).toHaveText("0");
  await expect(c.locator('[data-text="investigationLifecycle"]')).toHaveText("No investigation");
  // A reload of one tab loses its unsaved change and does not pick up the other's.
  await b.reload();
  await ready(b);
  await expect(version(b)).toHaveText("0");
  await expect(b.locator("[data-causa-save-indicator]")).toBeHidden();
  await expect(version(a)).toHaveText("3");
};

test("tabs independent with and without navigator.locks", async ({ browser }) => {
  for (const withoutLocks of [false, true]) {
    const tabs = await twoTabs(browser, withoutLocks);
    try {
      await independent(tabs);
    } finally {
      await tabs.context.close();
    }
  }
});
