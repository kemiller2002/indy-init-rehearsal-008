// Owner: ui (T16). UI-011: keyboard, accessibility and iPad usability, on the application shell (FL-01, FL-02,
// FL-04, FL-05). Screens that later tasks add run the same assertions in their own specs.
import { expect, sitePath, test } from "../support/fixtures.js";
import {
  axeClean,
  focusVisible,
  noHorizontalScroll,
  reducedMotion,
  skipLinkFirst,
  touchTargets
} from "../support/layout-assertions.mjs";

const investigation = "#/investigations/0f8fad5b-d9cb-469f-a165-70867728950e?repo=kemiller2002/causa-data";
const viewports = [
  { width: 320, height: 640 },
  { width: 820, height: 1180 },
  { width: 1180, height: 820 },
  { width: 1440, height: 900 }
];

// A fresh document load, so keyboard assertions start from the top of the page (a hash-only goto keeps focus).
const fresh = async (page, causa, hash) => {
  await page.goto("about:blank");
  await causa.open(hash);
};

test("UI-011-O1", async ({ page, causa }) => {
  test.setTimeout(300_000);
  for (const viewport of viewports) {
    await page.setViewportSize(viewport);
    for (const hash of ["#/", investigation, "#/settings"]) {
      await fresh(page, causa, hash);
      expect(await touchTargets(page), `targets ${hash} at ${viewport.width}`).toEqual([]);
      expect(await skipLinkFirst(page), `skip link ${hash} at ${viewport.width}`).toEqual([]);
      await fresh(page, causa, hash);
      expect(await focusVisible(page), `focus ${hash} at ${viewport.width}`).toEqual([]);
    }
  }
  // Landmarks, labelled navigation, a semantic status region, and no automated WCAG A/AA violation.
  await page.setViewportSize({ width: 1440, height: 900 });
  await fresh(page, causa, investigation);
  await expect(page.getByRole("main")).toHaveCount(1);
  await expect(page.getByRole("banner").filter({ visible: true })).toHaveCount(1);
  await expect(page.locator(".causa-masthead [role=status]")).toHaveText("Causa engine ready");
  expect(await axeClean(page)).toEqual([]);
  // Skip to content moves focus to the main region and keeps the route.
  await page.keyboard.press("Tab");
  await page.keyboard.press("Enter");
  await expect(page.locator("#causa-main")).toBeFocused();
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Overview");
});

test("UI-011-O2", async ({ page, causa }) => {
  for (const viewport of viewports) {
    await page.setViewportSize(viewport);
    for (const hash of ["#/", investigation, "#/nope"]) {
      await causa.open(hash);
      expect(await noHorizontalScroll(page), `${hash} at ${viewport.width}`).toEqual([]);
    }
  }
  // 200% text (VP-200-TEXT): the root font size doubles and the shell still reflows without page scrolling.
  await page.setViewportSize({ width: 1280, height: 800 });
  await causa.open(investigation);
  await page.addStyleTag({ content: "html { font-size: 200% !important; }" });
  expect(await noHorizontalScroll(page)).toEqual([]);
  await expect(page.getByRole("navigation", { name: "Investigation" })).toBeVisible();
  // Reduced motion (FL-05): the loading indicator does not animate when the person asks for less motion.
  await page.emulateMedia({ reducedMotion: "reduce" });
  await page.route("**/_framework/dotnet.native.wasm", () => new Promise(() => {}));
  await page.goto(sitePath);
  expect(await reducedMotion(page)).toEqual([]);
});

test("UI-011-O3", async ({ page, causa }) => {
  // Every shell operation works by keyboard alone: no drag, no hover.
  await page.setViewportSize({ width: 1440, height: 900 });
  await causa.open(investigation);
  const evidence = page.getByRole("navigation", { name: "Investigation" }).getByRole("link", { name: "Evidence" });
  await evidence.focus();
  await page.keyboard.press("Enter");
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Evidence");
  await page.setViewportSize({ width: 320, height: 640 });
  const summary = page.locator(".ef-navigation-shell__compact summary");
  await summary.focus();
  await page.keyboard.press("Enter");
  const settings = page.locator(".ef-navigation-shell__compact nav").getByRole("link", { name: "Settings" });
  await expect(settings).toBeVisible();
  await settings.focus();
  await page.keyboard.press("Enter");
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Settings");
});

test("UI-011-O4", async ({ page, causa }) => {
  // Focus survives route transitions, and Back restores the previous view.
  await causa.open(investigation);
  const timeline = page.getByRole("navigation", { name: "Investigation" }).getByRole("link", { name: "Timeline" });
  await timeline.focus();
  await page.keyboard.press("Enter");
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Timeline");
  await expect(timeline).toBeFocused();
  await page.goBack();
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Overview");
});
