// Owner: ui (WI-R8-UTESTS). UI-004: command availability and submission. Every availability, reason and decision
// comes from the F# engine through Limen; the page only renders it and sends typed commands.
import { expect, test } from "../support/fixtures.js";

const workspace = (page) => page.locator("[data-causa-workspace]");
const button = (page, name) => workspace(page).getByRole("button", { name, exact: true });
const outcome = (page) => workspace(page).locator("[data-causa-outcome]");
const version = (page) => workspace(page).locator("[data-causa-version]");

test("UI-004-O2", async ({ page, causa, failures }) => {
  await causa.open("#/investigations/new");
  await button(page, "Start investigation").click();
  await expect(version(page)).toHaveText("1");
  // A prohibited action carries the engine's reason in words, and never reads as available.
  const activate = workspace(page).locator('[data-causa-capability="ActivateInvestigation"]');
  await expect(activate).toContainText("Unavailable:");
  await expect(activate).toContainText("Record at least one observation before activating");
  await expect(activate).not.toContainText("Available.");
  await expect(workspace(page).locator('[data-causa-capability="StartInvestigation"]')).toHaveText(/^Unavailable: .+/);
  // Pressing it anyway is refused with the reason; nothing changes and nothing says it was done.
  await button(page, "Activate").click();
  await expect(outcome(page)).toHaveAttribute("data-outcome", "refused");
  await expect(outcome(page)).toContainText("Activate was not done:");
  await expect(outcome(page)).not.toContainText("done.");
  await expect(version(page)).toHaveText("1");
  await expect(workspace(page).locator("[data-causa-lifecycle]")).toHaveText("Draft");
  expect(failures).toEqual([]);
});

test("UI-004-O5", async ({ page, causa }) => {
  await causa.open("#/investigations/new");
  // Keyboard invocation: focus and Enter / Space submit the command, exactly as a click does.
  const start = button(page, "Start investigation");
  await start.focus();
  await expect(start).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(version(page)).toHaveText("1");
  const observe = button(page, "Record an observation");
  await observe.focus();
  await page.keyboard.press("Space");
  await expect(version(page)).toHaveText("2");
  // Tab reaches every command button in order.
  const names = ["Start investigation", "Record an observation", "Activate", "Record a hypothesis", "Record a conclusion", "Resolve"];
  await button(page, names[0]).focus();
  for (const name of names.slice(1)) {
    await page.keyboard.press("Tab");
    await expect(button(page, name)).toBeFocused();
  }
  // Touch targets: every command button is at least 24 by 24 CSS pixels (WCAG 2.2 target size).
  for (const name of names) {
    const box = await button(page, name).boundingBox();
    expect(box, name).not.toBeNull();
    expect(box.width, `${name} width`).toBeGreaterThanOrEqual(24);
    expect(box.height, `${name} height`).toBeGreaterThanOrEqual(24);
  }
});

test("UI-004-O6", async ({ page, causa }) => {
  await causa.open("#/investigations/new");
  const sent = [];
  await page.exposeFunction("__causaSent", (name) => sent.push(name));
  await page.evaluate(() => {
    document.addEventListener("click", (e) => {
      const el = e.target.closest("[data-event]");
      if (el) window.__causaSent(el.getAttribute("data-event"));
    }, true);
  });
  await button(page, "Start investigation").click();
  await expect(version(page)).toHaveText("1");
  // The page does not pre-filter: an unavailable command is still sent, and the kernel decides it. Its refusal names
  // the kernel's blocker codes, and the capability text is the same reason the kernel gives.
  for (const [name, command] of [["Activate", "ActivateInvestigation"], ["Resolve", "ResolveInvestigation"], ["Start investigation", "StartInvestigation"]]) {
    await expect(workspace(page).locator(`[data-causa-capability="${command}"]`)).toContainText("Unavailable:");
    await button(page, name).click();
    await expect(outcome(page)).toHaveAttribute("data-outcome", "refused");
    await expect(outcome(page)).toContainText(`${name} was not done:`);
    expect(await outcome(page).getAttribute("data-blocker-codes")).toMatch(/\S/);
    expect(sent).toContain(`submit.${command}`);
  }
  await expect(version(page)).toHaveText("1");
  // Once the engine's state allows it, the same button is accepted: availability follows the projection alone.
  await button(page, "Record an observation").click();
  await expect(workspace(page).locator('[data-causa-capability="ActivateInvestigation"]')).toHaveText("Available");
  await button(page, "Activate").click();
  await expect(outcome(page)).toHaveAttribute("data-outcome", "accepted");
});
