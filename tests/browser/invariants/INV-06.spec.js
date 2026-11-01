// Owner: ui (WI-R8-UIP0). INV-06: no investigation content, unsaved change, save state, branch head, write marker or
// credential is written to any browser store or cross-tab channel.
import { expect, test } from "../support/fixtures.js";

test("INV-06", async ({ page, causa }) => {
  // Record every cross-tab message and store write the page attempts, from before the app starts.
  await page.addInitScript(() => {
    window.__causaChannel = [];
    const Native = window.BroadcastChannel;
    if (Native) window.BroadcastChannel = class extends Native { postMessage(m) { window.__causaChannel.push(String(m)); return super.postMessage(m); } };
    window.addEventListener("storage", () => window.__causaChannel.push("storage-event"));
  });
  await causa.open("#/investigations/new");
  const ws = page.locator("[data-causa-workspace]");
  for (const name of ["Start investigation", "Record an observation", "Activate", "Record a hypothesis"]) {
    await ws.getByRole("button", { name, exact: true }).click();
  }
  await expect(page.locator("[data-causa-save-state]")).toHaveText("Unsaved");
  const stores = await page.evaluate(async () => ({
    local: Object.keys(localStorage),
    session: Object.keys(sessionStorage),
    cookie: document.cookie,
    idb: indexedDB.databases ? (await indexedDB.databases()).map((d) => d.name) : [],
    caches: "caches" in window ? await caches.keys() : [],
    channel: window.__causaChannel
  }));
  expect(stores).toEqual({ local: [], session: [], cookie: "", idb: [], caches: [], channel: [] });
  // A reload starts from nothing: nothing survived in any store.
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-engine", "ready", { timeout: 30_000 });
  await expect(page.locator('[data-text="projectionVersion"]')).toHaveText("0");
});
