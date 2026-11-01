// Owner: ui (WI-R8-UIP0). INV-10: two tabs are independent; an unsaved change in one never appears in another.
import { expect, sitePath, test } from "../support/fixtures.js";

test("INV-10", async ({ page, context, causa }) => {
  await causa.open("#/investigations/new");
  const other = await context.newPage();
  await other.goto(`${sitePath}#/investigations/new`);
  await expect(other.locator("html")).toHaveAttribute("data-engine", "ready", { timeout: 30_000 });
  const ws = page.locator("[data-causa-workspace]");
  await ws.getByRole("button", { name: "Start investigation", exact: true }).click();
  await ws.getByRole("button", { name: "Record an observation", exact: true }).click();
  await expect(page.locator('[data-text="projectionVersion"]')).toHaveText("2");
  // The other tab, open the whole time and after a fresh navigation, still has no investigation.
  await expect(other.locator('[data-text="projectionVersion"]')).toHaveText("0");
  await expect(other.locator('[data-text="investigationLifecycle"]')).toHaveText("No investigation");
  await other.reload();
  await expect(other.locator("html")).toHaveAttribute("data-engine", "ready", { timeout: 30_000 });
  await expect(other.locator('[data-text="projectionVersion"]')).toHaveText("0");
  await expect(other.locator("[data-causa-save-indicator]")).toBeHidden();
});
