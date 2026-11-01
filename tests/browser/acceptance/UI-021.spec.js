// Owner: ui (WI-R8-UIP0). UI-021: content and route verification across the three destinations.
import { expect, test } from "../support/fixtures.js";

const destinations = [["#/", "Investigations"], ["#/administration", "Administration"], ["#/settings", "Settings"]];

test("UI-021-O1", async ({ page, causa }) => {
  const logged = [];
  page.on("console", (m) => logged.push(m.text()));
  for (const [route, title] of destinations) {
    await causa.open(route);
    // (4) labels and navigation: the destination's heading, title and current navigation item agree.
    await expect(page.getByRole("heading", { level: 1 })).toHaveText(title);
    await expect(page).toHaveTitle(`${title} · Causa`);
    await expect(page.getByRole("navigation", { name: "Primary" }).locator('[aria-current="page"]')).toHaveText(title);
    // (1) no catalog/replay paragraph or implementation prose on routine views.
    const text = await page.evaluate(() => document.body.innerText);
    expect(text).not.toMatch(/historical-v1|\breplay/i);
    // (5) no credential or secret value rendered.
    expect(await page.content()).not.toMatch(/gh[pousr]_[A-Za-z0-9]{20,}|github_pat_/);
  }
  // (2) operational details appear in Administration (connection, build).
  await causa.open("#/administration");
  await expect(page.getByRole("main")).toContainText(/Not connected/);
  await expect(page.getByRole("main")).toContainText(/\b[0-9a-f]{7,40}\b/);
  // (5) nothing secret logged.
  expect(logged.join("\n")).not.toMatch(/gh[pousr]_[A-Za-z0-9]{20,}|github_pat_|Authorization/i);
});

test("UI-021-O2", async ({ page, causa }) => {
  // Placement, not existence: investigation destinations never appear in the global navigation, operational content
  // never appears in Settings, and investigation content never appears in Administration.
  await causa.open("#/settings");
  expect(await page.getByRole("main").innerText()).not.toMatch(/Connection|diagnostic|\b[0-9a-f]{7,40}\b/i);
  await causa.open("#/administration");
  expect(await page.getByRole("main").innerText()).not.toMatch(/Observation|Hypothes|Start an investigation/);
  await expect(page.getByRole("navigation", { name: "Primary" }).getByRole("link", { name: "Evidence" })).toHaveCount(0);
});
