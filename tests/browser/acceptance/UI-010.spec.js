// Owner: ui (WI-R8-UIP0). UI-010: empty, error and recovery states.
import { expect, test } from "../support/fixtures.js";

const unloaded = "#/investigations/0f8fad5b-d9cb-469f-a165-70867728950e?repo=kemiller2002/causa-data";

test("UI-010-O1", async ({ page, causa }) => {
  // No investigations: a specific empty state with a next step.
  await causa.open("#/");
  const main = page.getByRole("main");
  await expect(main).toContainText("No investigation is open in this tab");
  await expect(main.getByRole("button", { name: "Start an investigation" }).or(main.getByRole("link", { name: "Start an investigation" }))).toBeVisible();
  // No evidence: the evidence region says so instead of showing nothing.
  await causa.open("#/investigations/new");
  await expect(page.locator("[data-causa-workspace]")).toContainText("No evidence recorded yet.");
  // An address that names nothing: a recoverable state with a way back.
  await causa.open("#/nonsense");
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Page not found");
  await page.getByRole("link", { name: "Go to Investigations" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Investigations");
});

test("UI-010-O2", async ({ page, causa }) => {
  // An investigation that cannot be shown says what happened, what is preserved and a safe next step. Unsaved changes
  // live only in this tab's memory, and the page says so (PERSIST-020).
  await causa.open("#/investigations/new");
  await page.locator("[data-causa-workspace]").getByRole("button", { name: "Start investigation", exact: true }).click();
  await expect(page.getByRole("main")).toContainText("exists only in this tab");
  await causa.open(unloaded);
  const main = page.getByRole("main");
  await expect(main).toContainText("This investigation has not been loaded in this tab.");
  // The safe next step: an action that loads it (or connects the repository that holds it).
  await expect(main.getByRole("button", { name: /Connect|Open|Load/ }).or(main.getByRole("link", { name: /Connect|Open|Load/ }))).toBeVisible();
});

test("UI-010-O5", async ({ page, causa }) => {
  // No internal secret on any routine page: no token-shaped string, no Authorization detail.
  for (const route of ["#/", "#/investigations/new", unloaded, "#/administration", "#/settings", "#/nonsense"]) {
    await causa.open(route);
    const html = await page.content();
    expect(html, route).not.toMatch(/gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|Bearer\s+[A-Za-z0-9._-]{10,}/);
  }
});
