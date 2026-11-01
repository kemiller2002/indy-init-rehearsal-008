// Owner: ui (WI-R8-UIP0, WI-R8-UTESTS). INV-06: no investigation content, unsaved change, save state, branch head,
// write marker or credential is written to any browser store or cross-tab channel, and no Web Locks API is used.
import { expect, test } from "../support/fixtures.js";

test("every store empty of investigation content", async ({ page, causa }) => {
  // Record every cross-tab message and store write the page attempts, from before the app starts.
  await page.addInitScript(() => {
    window.__causaChannel = [];
    const Native = window.BroadcastChannel;
    if (Native) window.BroadcastChannel = class extends Native { postMessage(m) { window.__causaChannel.push(String(m)); return super.postMessage(m); } };
    window.addEventListener("storage", () => window.__causaChannel.push("storage-event"));
    // INV-06: no Web Locks API is used (correctness never depends on cross-tab coordination).
    window.__causaLocks = [];
    if (navigator.locks) {
      const locks = navigator.locks;
      for (const name of ["request", "query"]) {
        const native = locks[name].bind(locks);
        locks[name] = (...args) => { window.__causaLocks.push(name); return native(...args); };
      }
    }
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
    opfs: navigator.storage && navigator.storage.getDirectory
      ? await (async () => {
          // WebKit refuses OPFS in an ephemeral (Playwright) context with UnknownError: then no page can store there.
          const root = await navigator.storage.getDirectory().catch((e) => (e && e.name === "UnknownError" ? null : Promise.reject(e)));
          const names = [];
          if (root) for await (const k of root.keys()) names.push(k);
          return names;
        })()
      : [],
    channel: window.__causaChannel,
    locks: window.__causaLocks
  }));
  expect(stores).toEqual({ local: [], session: [], cookie: "", idb: [], caches: [], opfs: [], channel: [], locks: [] });
  // A reload starts from nothing: nothing survived in any store.
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-engine", "ready", { timeout: 30_000 });
  await expect(page.locator('[data-text="projectionVersion"]')).toHaveText("0");
});
