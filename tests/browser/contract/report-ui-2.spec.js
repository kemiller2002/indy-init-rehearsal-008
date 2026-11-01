// Owner: ui-2 (WI-R8-T26). IF41-REPORT contract (ui-2 consumer): the report preview (S22) and its print export
// through Folio's print components (@echelon-foundry/print-components 0.4.1; R12.1-R12.3, UI-009).
//   - the report is a projection of the kernel's replayed history: the same version every other region reads
//     (ADRP-004), its chronology the accepted events in canonical version order, with their event ids;
//   - an unresolved investigation renders as an interim report that lists its unresolved work (R12.3);
//   - every section the kernel does not record says "Not recorded" and no conclusion is presented as verified
//     (UI-009-O4); the kernel records no causal link, and the report says so;
//   - print shows only the report, laid out by Folio's print stylesheet, legible in grayscale, and the print is
//     stamped with its generation time (UI-009-O2, UI-009-O3).
import { expect, test } from "../support/fixtures.js";

const submit = (page, name) => page.locator("[data-causa-workspace]").getByRole("button", { name, exact: true }).click();
const outcome = (page) => page.locator("[data-causa-outcome]");
const region = (page) => page.locator("[data-causa-region='report']");
const report = (page) => page.locator("[data-causa-report]");

const accept = async (page, button) => {
  await submit(page, button);
  await expect(outcome(page)).toHaveAttribute("data-outcome", "accepted");
};

const headings = [
  "Source investigation",
  "Observation",
  "Chronology",
  "Hypotheses considered",
  "Evidence",
  "Experiments",
  "Falsified explanations",
  "Conclusion posture and root-cause reasoning",
  "Corrective action",
  "Verification",
  "Classification and redaction"
];
const unrecorded = ["Evidence", "Experiments", "Falsified explanations", "Corrective action", "Verification"];

// Every sentence of the report that speaks of verification denies it (UI-009-O4: no invented verified conclusion).
const verificationClaims = (text) =>
  text
    .split(/(?<=[.:;])\s+/)
    .filter((sentence) => /verif/i.test(sentence))
    .filter((sentence) => !/\b(no|none|not)\b/i.test(sentence));

// WCAG relative luminance of an rgb() colour after a grayscale conversion, and the contrast of two of them.
const grayscaleContrasts = (page) =>
  report(page).evaluate((root) => {
    const parse = (c) => (c.match(/[\d.]+/g) || []).map(Number);
    const channel = (v) => {
      const s = v / 255;
      return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
    };
    const grayLuminance = ([r, g, b]) => {
      const y = 0.2126 * r + 0.7152 * g + 0.0722 * b; // the grey a grayscale printer gives
      return channel(y);
    };
    const background = (e) => {
      for (let n = e; n; n = n.parentElement) {
        const [r, g, b, a = 1] = parse(getComputedStyle(n).backgroundColor);
        if (a > 0) return [r, g, b];
      }
      return [255, 255, 255]; // paper
    };
    return [...root.querySelectorAll("*")]
      .filter((e) => e.offsetParent !== null || getComputedStyle(e).position === "fixed")
      .filter((e) => [...e.childNodes].some((n) => n.nodeType === 3 && n.textContent.trim() !== ""))
      .map((e) => {
        const fg = grayLuminance(parse(getComputedStyle(e).color));
        const bg = grayLuminance(background(e));
        const [hi, lo] = fg > bg ? [fg, bg] : [bg, fg];
        return { text: e.textContent.trim().slice(0, 40), contrast: (hi + 0.05) / (lo + 0.05) };
      });
  });

test("renders the projection", async ({ page, causa, failures }) => {
  await causa.open("#/investigations/new");
  // No investigation: no report, and the page says so.
  await expect(region(page).locator("[data-causa-report-summary]")).toHaveText("No report yet: start an investigation to preview its report.");
  await expect(report(page)).toBeHidden();
  await expect(region(page).locator("[data-causa-print]")).toBeHidden();

  // An active investigation with one observation: an interim report (R12.3).
  for (const button of ["Start investigation", "Record an observation", "Activate"]) await accept(page, button);
  await expect(report(page)).toBeVisible();
  await expect(region(page)).toHaveAttribute("data-projection-version", "3");
  await expect(report(page)).toHaveAttribute("data-report-kind", "interim");
  await expect(report(page).locator("[data-causa-report-kind]")).toHaveText("Interim report");
  await expect(report(page).locator("[data-causa-report-version]")).toHaveText("3");
  await expect(report(page).locator("[data-causa-report-lifecycle]")).toHaveText("Active");
  await expect(report(page).locator("[data-causa-report-investigation]")).toHaveText(/^[0-9a-f-]{36}$/);
  const unresolved = report(page).locator("[data-causa-report-unresolved] li");
  await expect(unresolved.first()).toHaveText("The investigation is Active, not resolved: this is an interim report.");
  await expect(unresolved.nth(1)).toHaveText("No conclusion is recorded.");

  // Every required section, in order; what the kernel does not record says so, with no invented content.
  const sections = report(page).locator("[data-causa-report-section]");
  await expect(sections.locator("h6")).toHaveText(headings);
  for (const heading of unrecorded) {
    const section = sections.filter({ has: page.locator("h6", { hasText: new RegExp(`^${heading}$`) }) });
    await expect(section).toHaveAttribute("data-recorded", "not-recorded");
    await expect(section.locator("p").first()).toHaveText(/^Not recorded\. The investigation kernel does not record /);
  }
  await expect(sections.nth(1)).toHaveAttribute("data-recorded", "recorded");
  await expect(sections.nth(1).locator("p").first()).toHaveText(/^1 observation recorded\. Recorded: Observation recorded at version 2\./);

  // The chronology: the accepted events, in canonical version order, with their event ids (provenance).
  const rows = report(page).locator("[data-causa-report-chronology] tbody tr");
  await expect(rows).toHaveCount(3);
  await expect(rows.locator("td:nth-child(1)")).toHaveText(["1", "2", "3"]);
  await expect(rows.locator("td:nth-child(2)")).toHaveText(["Investigation started", "Observation recorded", "Investigation activated"]);
  for (const id of await rows.locator("td:nth-child(3)").allTextContents()) expect(id).toMatch(/^[0-9a-f-]{36}$/);
  await expect(sections.nth(1).locator("ef-print-note")).toContainText(await rows.nth(1).locator("td:nth-child(3)").textContent());

  // A recorded conclusion is never presented as verified, and no cause is stated (the kernel records no causal link).
  await accept(page, "Record a conclusion");
  await expect(report(page).locator("[data-causa-report-version]")).toHaveText("4");
  const posture = sections.nth(7);
  await expect(posture.locator("p").first()).toContainText("1 conclusion recorded (Conclusion recorded at version 4); none is verified.");
  await expect(posture.locator("p").first()).toContainText("The kernel records no causal link and no root-cause reasoning");
  expect(verificationClaims(await report(page).innerText())).toEqual([]);

  // A refused command changes nothing in the report.
  const before = await report(page).innerText();
  await submit(page, "Start investigation");
  await expect(outcome(page)).toHaveAttribute("data-outcome", "refused");
  expect(await report(page).innerText()).toEqual(before);

  // Resolved: a final report, which still names what is unverified and unrecorded and invents no verified conclusion.
  await accept(page, "Resolve");
  await expect(report(page)).toHaveAttribute("data-report-kind", "final");
  await expect(report(page).locator("[data-causa-report-kind]")).toHaveText("Final report");
  await expect(report(page).locator("[data-causa-report-version]")).toHaveText("5");
  await expect(unresolved.first()).toHaveText("No conclusion is verified: the kernel records no verification outcome.");
  await expect(rows).toHaveCount(5);
  expect(verificationClaims(await report(page).innerText())).toEqual([]);

  // Export: the print button prints, stamped with its generation time (UI-009-O2).
  await page.evaluate(() => {
    window.__causaPrints = 0;
    window.print = () => {
      window.__causaPrints += 1;
    };
  });
  await region(page).locator("[data-causa-print]").click();
  expect(await page.evaluate(() => window.__causaPrints)).toBe(1);
  await expect(report(page).locator("[data-causa-report-generated]")).toHaveText(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/);

  // Print media: only the report, laid out by Folio's print stylesheet (UI-009-O3).
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue("--ef-print-page-margin").trim())).toBe("");
  await page.emulateMedia({ media: "print" });
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue("--ef-print-page-margin").trim())).toBe("0.7in");
  await expect(report(page)).toBeVisible();
  await expect(sections).toHaveCount(headings.length);
  for (const other of [".causa-masthead", ".causa-shell nav", "[data-causa-region='capabilities']", "[data-causa-region='graph']", "[data-causa-print]"]) {
    await expect(page.locator(other).first(), `${other} is left out of the print`).toBeHidden();
  }
  // Legible in grayscale: every text of the printed report keeps WCAG AA contrast once colour is removed.
  const contrasts = await grayscaleContrasts(page);
  expect(contrasts.length).toBeGreaterThan(20);
  expect(contrasts.filter((c) => c.contrast < 4.5)).toEqual([]);
  // The PDF export itself (Chromium's print to PDF).
  const pdf = await page.pdf({ printBackground: false });
  expect(pdf.subarray(0, 5).toString()).toBe("%PDF-");
  expect(pdf.length).toBeGreaterThan(5_000);
  await page.emulateMedia({ media: "screen" });

  expect(failures).toEqual([]);
});
