// Owner: integration. Requirement probes for the investigation workflow (CAT-CAUSA-INVESTIGATION, kemiller2002/Indy-init
// requirements/catalogs/causa-investigation.requirement-catalog.json). Each test drives the real deployed page and
// records facts for the manifest's obligations; Dokimos decides the verdicts. The tests fail only when the page could
// not be driven at all (engine did not boot, a step threw): a missing or broken requirement is a recorded fact, so it
// reaches the verifier as FAIL instead of hiding behind an aborted run.
import { expect, test } from "../support/fixtures.js";
import { fakeGitHub, OWNER, REPO, TOKEN } from "./support/fake-github.js";
import {
  elementFacts,
  manifest,
  recorder,
  requirement,
} from "./support/probe.js";

test.skip(
  manifest === null,
  "REQUIREMENT_MANIFEST is not set: no canonical manifest to probe",
);

const statement =
  "The nightly build failed with 'disk full' on agent 7 after the cache change.";
const title = "Nightly build disk full";

const text = async (locator) =>
  (await locator.count()) === 0
    ? ""
    : ((await locator.first().textContent()) ?? "").trim();
const lifecycle = (page) => page.locator("[data-causa-lifecycle]");
const workspace = (page) => page.locator("[data-causa-workspace]");
const count = (page, key) =>
  text(page.locator(`[data-causa-workspace] [data-text="${key}"]`));
const saveState = (page) =>
  page.locator("[data-causa-save-indicator] [data-causa-save-state]");

/** Click the element carrying a requirement ID if it is there and enabled; report whether it was activated. */
const activate = async (page, id) => {
  const target = page.locator(`[data-requirement-id="${id}"]`);
  if (
    (await target.count()) !== 1 ||
    !(await target.isVisible()) ||
    !(await target.isEnabled())
  )
    return false;
  await target.click();
  return true;
};

/** Fill a field carrying a requirement ID and read back what it then holds ("" when there is no such field). */
const enter = async (page, id, value) => {
  const field = page.locator(`[data-requirement-id="${id}"]`);
  if ((await field.count()) !== 1 || !(await field.isEditable())) return "";
  await field.fill(value);
  return field.inputValue();
};

const connect = async (page) => {
  const form = page.locator("[data-causa-connect-form]");
  await form.getByLabel("Owner").fill(OWNER);
  await form.getByLabel("Repository").fill(REPO);
  await form.getByLabel("Token").fill(TOKEN);
  await form.getByRole("button", { name: "Connect" }).click();
  await expect(page.locator("[data-causa-connect-status]")).toHaveText(
    "Connected to GitHub (read and write)",
  );
};

test("investigation start and workspace: fields, actions and outcomes in this tab", async ({
  page,
  causa,
}, testInfo) => {
  const probe = recorder(testInfo);
  try {
    await causa.open("#/investigations/new");

    // S01: no domain profile can be selected in Causa (it has no profile selector), so INV-003 does not apply.
    probe.facts["domain-profile-selected"] =
      (await page.locator("[data-causa-domain-profile]").count()) > 0;

    await probe.elements(page, "INV-001", [
      "exists",
      "visible",
      "accessible",
      "editable",
    ]);
    probe.record("INV-001", "enter", {
      elements: await elementFacts(page, "INV-001"),
      attempts: [
        {
          value: statement,
          observed: await enter(page, "INV-001", statement),
          accepted: true,
        },
      ],
    });
    await probe.elements(page, "INV-002", [
      "exists",
      "visible",
      "accessible",
      "editable",
    ]);
    probe.record("INV-002", "enter", {
      elements: await elementFacts(page, "INV-002"),
      attempts: [
        {
          value: title,
          observed: await enter(page, "INV-002", title),
          accepted: true,
        },
      ],
    });
    await probe.elements(page, "INV-003", ["exists", "visible"]);
    await probe.elements(page, "INV-005", ["exists", "visible", "accessible"]);

    const start = await probe.elements(page, "INV-004", [
      "exists",
      "visible",
      "accessible",
    ]);
    await activate(page, "INV-004");
    const started = await text(lifecycle(page));
    probe.record("INV-004", "activate", {
      elements: start,
      expected: "Draft",
      observed: started,
    });
    probe.record("INV-004", "outcome", {
      expected: "Draft",
      observed: started,
    });
    await probe.elements(page, "INV-018", ["exists", "visible"]);
    probe.record("INV-018", "outcome", {
      expected: "Draft",
      observed: started,
    });

    // R1.2 / A96: the original statement is inspectable after the start, exactly as entered.
    await probe.elements(page, "INV-008", ["exists", "visible"]);
    probe.record("INV-008", "outcome", {
      expected: statement,
      observed: await text(page.locator('[data-requirement-id="INV-008"]')),
    });
    await probe.elements(page, "INV-007", ["exists", "visible"]);

    const observe = await probe.elements(page, "INV-014", [
      "exists",
      "visible",
      "accessible",
    ]);
    await activate(page, "INV-014");
    const observations = await count(page, "observationCount");
    probe.record("INV-014", "activate", {
      elements: observe,
      expected: "1",
      observed: observations,
    });
    probe.record("INV-014", "outcome", {
      expected: "1",
      observed: observations,
    });

    await workspace(page)
      .getByRole("button", { name: "Activate", exact: true })
      .click();
    const hypothesise = await probe.elements(page, "INV-009", [
      "exists",
      "visible",
      "accessible",
    ]);
    await activate(page, "INV-009");
    const hypotheses = await count(page, "hypothesisCount");
    probe.record("INV-009", "activate", {
      elements: hypothesise,
      expected: "1",
      observed: hypotheses,
    });
    probe.record("INV-009", "outcome", { expected: "1", observed: hypotheses });
    await probe.elements(page, "INV-010", ["exists", "visible"]);
    const listed = await text(page.locator('[data-requirement-id="INV-010"]'));
    probe.record("INV-010", "outcome", {
      expected: "lists Hypothesis 1",
      observed: listed.includes("Hypothesis 1") ? "lists Hypothesis 1" : listed,
    });

    // S04 "what it explains" and S05 "original content": the fields a person types a hypothesis and evidence into.
    await probe.elements(page, "INV-011", ["exists", "visible", "editable"]);
    probe.record("INV-011", "enter", {
      elements: [],
      attempts: [
        {
          value: "The cache change filled the disk.",
          observed: await enter(
            page,
            "INV-011",
            "The cache change filled the disk.",
          ),
          accepted: true,
        },
      ],
    });
    await probe.elements(page, "INV-012", ["exists", "visible", "editable"]);
    probe.record("INV-012", "enter", {
      elements: [],
      attempts: [
        {
          value: "df -h: 100% on /var",
          observed: await enter(page, "INV-012", "df -h: 100% on /var"),
          accepted: true,
        },
      ],
    });
    await probe.elements(page, "INV-013", ["exists", "visible"]);
  } finally {
    // Whatever happened, the facts recorded so far reach the verifier; a failed test makes them FAIL there.
    probe.flush();
  }
});

test("investigation lifecycle: identifier rules, save, and reopening from GitHub", async ({
  page,
  causa,
}, testInfo) => {
  const probe = recorder(testInfo);
  try {
    const github = await fakeGitHub(page, { push: true });
    await causa.open("#/investigations/new");
    probe.facts["domain-profile-selected"] = false;

    await enter(page, "INV-001", statement);
    await enter(page, "INV-002", title);
    await connect(page);

    // INV-006: the identifier field, and its rule (a UUID, from the manifest's pattern) on Open.
    await probe.elements(page, "INV-006", [
      "exists",
      "visible",
      "accessible",
      "editable",
    ]);
    await page
      .locator("[data-causa-investigation-form]")
      .getByRole("button", { name: "Create", exact: true })
      .click();
    await expect(saveState(page)).toHaveText("Saved");
    // The created investigation's identity, from the files GitHub now holds.
    const uuid = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/;
    const id =
      [...github.repo.files.keys()]
        .map((path) => path.match(uuid)?.[0])
        .find(Boolean) ?? "";
    probe.record("INV-006", "enter", {
      elements: await elementFacts(page, "INV-006"),
      attempts: [
        {
          value: id,
          observed: await enter(page, "INV-006", id),
          accepted: true,
        },
      ],
    });

    // INV-001 / INV-002 save: is the text the person entered in what GitHub now holds?
    const stored = [...github.repo.files.values()].join("\n");
    probe.record("INV-001", "save", {
      expected: statement,
      observed: stored.includes(statement)
        ? statement
        : "(not in the saved investigation)",
    });
    probe.record("INV-002", "save", {
      expected: title,
      observed: stored.includes(title)
        ? title
        : "(not in the saved investigation)",
    });

    // INV-015 / INV-016: an unsaved change, then Save, confirmed only after GitHub's read-back.
    await activate(page, "INV-014");
    await expect(saveState(page)).toHaveText("Unsaved");
    await probe.elements(page, "INV-016", ["exists", "visible", "accessible"]);
    const save = await probe.elements(page, "INV-015", [
      "exists",
      "visible",
      "accessible",
    ]);
    await activate(page, "INV-015");
    await expect(saveState(page)).toHaveText(/Saved|Not saved|Unsaved/);
    const after = await text(saveState(page));
    probe.record("INV-015", "activate", {
      elements: save,
      expected: "Saved",
      observed: after,
    });
    probe.record("INV-015", "outcome", { expected: "Saved", observed: after });
    const savedState = `${await text(lifecycle(page))}|${await count(page, "observationCount")}`;

    // Reopen: a reload drops the tab's memory (R10.7); the in-memory GitHub keeps the commits.
    await page.reload();
    await expect(page.locator("html")).toHaveAttribute("data-engine", "ready", {
      timeout: 30_000,
    });
    await connect(page);
    const rules = requirement("INV-006").validation;
    const attempts = [];
    for (const value of ["", "not-a-uuid", id]) {
      await enter(page, "INV-006", value);
      await activate(page, "INV-017");
      const notice = await text(
        page.locator("[data-causa-investigation-notice]"),
      );
      attempts.push({
        value,
        observed: value,
        accepted: notice !== "Enter the investigation's identifier (a UUID).",
      });
    }
    probe.record("INV-006", "validation", { attempts });
    probe.record("INV-006", "rejects", {
      attempts: attempts.filter((a) => a.value !== id),
    });
    expect(
      rules.pattern,
      "the probe tries values against the manifest's own pattern",
    ).toBeTruthy();

    await expect(lifecycle(page))
      .not.toHaveText("No investigation", { timeout: 10_000 })
      .catch(() => undefined);
    const reopened = `${await text(lifecycle(page))}|${await count(page, "observationCount")}`;
    probe.record("INV-017", "reload", {
      expected: savedState,
      observed: reopened,
    });
    const original = page.locator('[data-requirement-id="INV-008"]');
    probe.record("INV-008", "reload", {
      expected: statement,
      observed: (await original.isVisible())
        ? await text(original)
        : "(not shown after reopening)",
    });
  } finally {
    probe.flush();
  }
});
