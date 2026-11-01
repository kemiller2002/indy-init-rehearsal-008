// Owner: integration (WI-R8-ITESTS2). AR-029 Start smoke on every primary route.
//
// AR-029-O1: the deployable build (dist/, opened exactly as published) starts in a real browser on every primary
// route of the route table (Routes.parse: home, start, every investigation tab, administration, settings and the
// not-found fallback): the F# engine boots through Limen, the route's own screen is the one shown, and the page
// raises no HTTP error, failed request, uncaught script error or console error. A .NET WebAssembly interpreter
// fault such as "locals size too big" (AR-029-O2) surfaces as a console error or page error and fails the test.
import { expect, test } from "../support/fixtures.js";

const investigation = "0b6f2a0e-3c1d-4f5e-9a7b-2c4d6e8f0a1b";
const tabs = ["overview", "causal-analysis", "evidence", "hypotheses", "timeline", "corrective-actions"];

const routes = [
  { hash: "", screen: "home" },
  { hash: "#/", screen: "home" },
  { hash: "#/investigations/new", screen: "investigation-start" },
  ...tabs.map((tab) => ({ hash: `#/investigations/${investigation}?repo=o/r&tab=${tab}`, screen: "investigation" })),
  { hash: "#/administration", screen: "administration" },
  { hash: "#/settings", screen: "settings" },
  { hash: "#/no-such-route", screen: "not-found" }
];

test("AR-029-O1", async ({ page, causa, failures }) => {
  const consoleErrors = [];
  page.on("console", (message) => {
    if (message.type() === "error") consoleErrors.push(message.text());
  });
  for (const { hash, screen } of routes) {
    await test.step(`starts on ${hash || "(no hash)"}`, async () => {
      await causa.open(hash);
      await expect(page.locator("[data-causa-screen]")).toHaveText(screen);
      await page.waitForLoadState("networkidle");
      expect(consoleErrors, `console errors on ${hash}`).toEqual([]);
      expect(failures, `failures on ${hash}`).toEqual([]);
    });
  }
});
