// Owner: ui (WI-R8-T22). PERSIST-032 No false persistence claims, on the deployable site: no state the browser can
// reach in this build (no repository connected) uses the words saved, synced, stored or safe for the investigation,
// promises local, queued or later saving, or shows Retry / Check again without a real action. Every state's copy is
// also scanned in Causa.Contract.Tests Save.ConsumerUi and the INV-14 invariant (ForbiddenPhrases).
import { expect, test } from "../support/fixtures.js";

const claimsSaved = /(?<!\bnot |\bnever |\bun)\b(saved|synced|stored|safe)\b/i;
const forbidden = /saved locally|on this device|queued|will be saved|saved later|save it later|sync later/i;

const visibleText = (page) => page.locator("[data-causa-app]").innerText();
const workspace = (page) => page.locator("[data-causa-workspace]");
const submit = (page, name) => workspace(page).getByRole("button", { name, exact: true }).click();

/** The page in each state the browser can reach: no investigation, then Unsaved after each kind of change. */
const reachableStates = async (page, causa) => {
  await causa.open("#/investigations/new");
  const states = [["no investigation", await visibleText(page)]];
  for (const name of ["Start investigation", "Record an observation", "Record a hypothesis"]) {
    await submit(page, name);
    await expect(workspace(page).locator("[data-causa-outcome]")).toHaveAttribute("data-outcome", "accepted");
    states.push([`after ${name}`, await visibleText(page)]);
  }
  return states;
};

test("PERSIST-032-O1", async ({ page, causa }) => {
  for (const [state, text] of await reachableStates(page, causa)) {
    expect(claimsSaved.test(text), `${state}: ${text.match(claimsSaved)?.[0]}`).toBe(false);
  }
});

test("PERSIST-032-O2", async ({ page, causa }) => {
  for (const [state, text] of await reachableStates(page, causa)) {
    expect(forbidden.test(text), `${state}: ${text.match(forbidden)?.[0]}`).toBe(false);
  }
});

test("PERSIST-032-O3", async ({ page, causa }) => {
  await causa.open("#/investigations/new");
  await submit(page, "Start investigation");
  await expect(page.locator("[data-causa-save-state]")).toHaveText("Unsaved");
  await expect(page.locator("[data-causa-save-text]")).toContainText("only in this tab");
});

test("PERSIST-032-O4", async ({ page, causa }) => {
  await causa.open("#/investigations/new");
  await submit(page, "Start investigation");
  // Nothing is connected, so neither Retry, Check again nor Save could act: none is shown (UI-010).
  for (const name of ["Retry", "Check again", "Save"]) {
    await expect(page.locator("[data-causa-save-indicator]").getByRole("button", { name, exact: true })).toBeHidden();
  }
});

test("PERSIST-032-O5", async ({ page, causa, failures }) => {
  // The whole page copy, including hidden template text, carries no forbidden phrase in any reachable state.
  await causa.open("#/investigations/new");
  await submit(page, "Start investigation");
  const all = await page.locator("[data-causa-app]").textContent();
  expect(forbidden.test(all), all.match(forbidden)?.[0]).toBe(false);
  expect(failures).toEqual([]);
});
