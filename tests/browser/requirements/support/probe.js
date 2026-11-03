// Owner: integration. Requirement probes: record what the rendered page actually did for each obligation of the
// canonical verification manifest (praxis.verification-manifest/1), as dokimos.requirement-observations/1.
//
// A probe records facts only (element tag, role, accessible name, visibility, disabled, values read back), never a
// verdict: Dokimos (`dokimos verification requirements`) decides PASS/FAIL against the manifest's own rules, and only
// for probes whose Playwright test actually executed and passed. The spec that records probes must not assert the
// requirement itself, so a missing field is recorded as a fact (no element) instead of aborting the run.
//
// Environment: REQUIREMENT_MANIFEST (the compiled manifest), REQUIREMENT_SOURCE_COMMIT (the commit under test),
// REQUIREMENT_RUN_ID, REQUIREMENT_OBSERVATIONS_DIR (default test-results/requirement-observations).
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join, relative } from "node:path";

const manifestPath = process.env.REQUIREMENT_MANIFEST;
export const manifest = manifestPath
  ? JSON.parse(readFileSync(manifestPath, "utf8"))
  : null;
const outDir =
  process.env.REQUIREMENT_OBSERVATIONS_DIR ??
  "test-results/requirement-observations";

/** The manifest's requirement, so probes derive their values from the requirement, not from the implementation. */
export const requirement = (id) =>
  manifest?.requirements.find((r) => r.id === id);

/** The obligation IDs a requirement declares at a check, e.g. obligationsOf("INV-001", "enter"). */
const obligationOf = (id, check) =>
  requirement(id)?.obligations.find((o) => o.check === check)?.id;

/** Facts about every element carrying the ID in the current page state. */
export const elementFacts = async (page, id) => {
  const located = page.locator(`[data-requirement-id="${id}"]`);
  const count = await located.count();
  const facts = [];
  for (let i = 0; i < count; i += 1) {
    const element = located.nth(i);
    const visible = await element.isVisible();
    const described = await element.evaluate((e) => {
      const tag = e.tagName.toLowerCase();
      const implicit = {
        textarea: "textbox",
        select: "combobox",
        button: "button",
        a: e.hasAttribute("href") ? "link" : "",
        dd: "definition",
        time: "time",
        p: "paragraph",
        section: "region",
        output: "status",
        input:
          {
            checkbox: "checkbox",
            radio: "radio",
            submit: "button",
            button: "button",
            range: "slider",
          }[e.type] ?? "textbox",
      };
      const byIds = (ids) =>
        ids
          .split(/\s+/)
          .map((ref) => document.getElementById(ref)?.textContent ?? "")
          .join(" ")
          .trim();
      const label =
        e.labels && e.labels.length > 0
          ? [...e.labels]
              .map((l) => l.textContent)
              .join(" ")
              .trim()
          : "";
      const contentNamed = [
        "button",
        "a",
        "dd",
        "p",
        "span",
        "time",
        "output",
        "li",
      ].includes(tag);
      const candidates = [
        e.getAttribute("aria-label") ?? "",
        e.getAttribute("aria-labelledby")
          ? byIds(e.getAttribute("aria-labelledby"))
          : "",
        label,
        contentNamed ? (e.textContent ?? "").trim() : "",
      ];
      const name = candidates.find((c) => c.trim() !== "") ?? "";
      return {
        tag,
        type: tag === "input" ? (e.getAttribute("type") ?? "text") : "",
        role: e.getAttribute("role") ?? implicit[tag] ?? "",
        accessibleName: name,
        disabled:
          e.disabled === true ||
          e.getAttribute("aria-disabled") === "true" ||
          e.closest("fieldset[disabled]") !== null,
        readOnly:
          (tag === "input" || tag === "textarea") && e.readOnly === true,
        instance: e.getAttribute("data-requirement-instance"),
        options: tag === "select" ? [...e.options].map((o) => o.value) : [],
      };
    });
    facts.push({ ...described, visible });
  }
  return facts;
};

export const recorder = (testInfo) => {
  const file = relative(testInfo.project.testDir, testInfo.file).replaceAll(
    "\\",
    "/",
  );
  // The Dokimos identity of this test as the Playwright JSON reporter names it (TestResults.parsePlaywrightJson).
  const test = `playwright:${file} > ${testInfo.titlePath.slice(1).join(" > ")} [${testInfo.project.name}]`;
  const probes = [];
  const facts = {};

  const record = (id, check, data) => {
    const obligation = obligationOf(id, check);
    // Only obligations the manifest declares are recorded: a probe cannot add or rename an obligation.
    if (obligation) probes.push({ obligation, test, ...data });
  };

  return {
    test,
    facts,
    record,
    /** Record the element facts of an ID for each of the given checks (exists, visible, accessible, editable...). */
    async elements(page, id, checks) {
      const elements = await elementFacts(page, id);
      for (const check of checks) record(id, check, { elements });
      return elements;
    },
    flush() {
      mkdirSync(outDir, { recursive: true });
      const name = `${testInfo.project.name}-${testInfo.titlePath
        .slice(1)
        .join("-")
        .replace(/[^A-Za-z0-9-]+/g, "_")}.json`;
      writeFileSync(
        join(outDir, name),
        JSON.stringify(
          {
            schema: "dokimos.requirement-observations/1",
            runId: process.env.REQUIREMENT_RUN_ID ?? "local",
            sourceCommit: process.env.REQUIREMENT_SOURCE_COMMIT ?? "",
            manifestHash: manifest?.contentHash ?? "",
            facts,
            probes,
          },
          null,
          2,
        ) + "\n",
      );
    },
  };
};
