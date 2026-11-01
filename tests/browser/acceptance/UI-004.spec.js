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

// ---- WI-R8-FINALUI (ui): O4. GitHub is served in memory with page.route (the one-branch repository o/r of UI-023).
const api = "https://api.github.com";
const cors = {
  "access-control-allow-origin": "*",
  "access-control-allow-headers": "authorization, accept, content-type, x-github-api-version",
  "access-control-allow-methods": "GET, POST, OPTIONS"
};
const oidOf = (n) => n.toString(16).padStart(40, "0");

const fakeGitHub = async (page) => {
  const repo = { commits: 1, head: oidOf(1), files: new Map(), commitAttempts: 0, fail: null };
  const trees = new Map([[repo.head, new Map()]]);
  await page.route(`${api}/**`, async (route) => {
    const request = route.request();
    if (request.method() === "OPTIONS") return route.fulfill({ status: 204, headers: cors });
    const url = new URL(request.url());
    const body = request.postData() ? JSON.parse(request.postData()) : null;
    const json = (b, status = 200) => route.fulfill({ status, headers: { ...cors, "content-type": "application/json" }, body: JSON.stringify(b) });
    const blob = (path) => (repo.files.has(path) ? { object: { text: repo.files.get(path), isTruncated: false } } : null);
    if (request.method() === "GET" && url.pathname === "/repos/o/r") {
      return json({ full_name: "o/r", default_branch: "main", permissions: { admin: false, maintain: false, push: true, triage: false, pull: true } });
    }
    if (request.method() === "POST" && url.pathname === "/graphql") {
      const variables = body.variables ?? {};
      if ((body.query ?? "").includes("createCommitOnBranch")) {
        repo.commitAttempts += 1;
        if (repo.fail === "rate") return json({ message: "API rate limit exceeded" }, 429);
        if (repo.fail === "auth") return json({ message: "Bad credentials" }, 401);
        const input = variables.input;
        if (input.expectedHeadOid !== repo.head) {
          return json({ data: { createCommitOnBranch: null }, errors: [{ type: "STALE_DATA", message: "stale" }] });
        }
        for (const a of input.fileChanges.additions ?? []) repo.files.set(a.path, Buffer.from(a.contents, "base64").toString("utf8"));
        repo.commits += 1;
        repo.head = oidOf(repo.commits);
        trees.set(repo.head, new Map(repo.files));
        return json({ data: { createCommitOnBranch: { commit: { oid: repo.head } } } });
      }
      const target = { oid: repo.head, manifest: blob(variables.manifest), events: blob(variables.events), snapshot: blob(variables.snapshot) };
      return json({ data: variables.oid !== undefined ? { repository: { object: target } } : { repository: { ref: { target } } } });
    }
    if (request.method() === "GET" && url.pathname === "/repos/o/r/git/ref/heads/main") {
      return json({ ref: "refs/heads/main", object: { type: "commit", sha: repo.head } });
    }
    const contents = "/repos/o/r/contents/";
    if (request.method() === "GET" && url.pathname.startsWith(contents)) {
      const path = decodeURIComponent(url.pathname.slice(contents.length));
      const text = trees.get(url.searchParams.get("ref"))?.get(path);
      if (text === undefined) return json({ message: "Not Found" }, 404);
      return json({ type: "file", path, sha: oidOf(path.length), encoding: "base64", content: Buffer.from(text, "utf8").toString("base64") });
    }
    return json({ message: "Not Found" }, 404);
  });
  /** Another writer commits to the branch (the head this tab read is now stale). */
  repo.externalWrite = () => {
    repo.files.set("other-writer.txt", "changed elsewhere");
    repo.commits += 1;
    repo.head = oidOf(repo.commits);
    trees.set(repo.head, new Map(repo.files));
  };
  return repo;
};

test("UI-004-O4", async ({ page, causa, failures }) => {
  const repo = await fakeGitHub(page);
  const indicator = page.locator("[data-causa-save-indicator]");
  const stateLabel = indicator.locator("[data-causa-save-state]");
  const observe = async () => {
    const before = Number(await version(page).textContent());
    await button(page, "Record an observation").click();
    await expect(version(page)).toHaveText(String(before + 1));
  };
  await causa.open("#/investigations/new");
  await button(page, "Start investigation").click();
  await expect(version(page)).toHaveText("1");
  // A domain fault is shown safely: the engine's reason in words, with no stack trace or internal exception text.
  await button(page, "Resolve").click();
  await expect(outcome(page)).toHaveAttribute("data-outcome", "refused");
  const fault = await outcome(page).innerText();
  expect(fault).toContain("Resolve was not done:");
  expect(fault).not.toMatch(/exception|stack|\bat [A-Z][\w.]+\(|undefined|null|\[object|<[a-z]+[ >]/i);
  expect(failures).toEqual([]);
  await causa.open("#/investigations/new");
  const form = page.locator("[data-causa-connect-form]");
  await form.getByLabel("Owner").fill("o");
  await form.getByLabel("Repository").fill("r");
  await form.getByLabel("Token").fill("ghp_R8FINALUIui004tokenNotShown");
  await form.getByRole("button", { name: "Connect" }).click();
  await expect(page.locator("[data-causa-connect-status]")).toHaveText("Connected to GitHub (read and write)");
  await page.locator("[data-causa-investigation-form]").getByRole("button", { name: "Create", exact: true }).click();
  await expect(stateLabel).toHaveText("Saved");

  // Duplicate submission is avoided: a double press of Save sends one commit, not two.
  await observe();
  const attempts = repo.commitAttempts;
  const commits = repo.commits;
  await indicator.getByRole("button", { name: "Save", exact: true }).dblclick();
  await expect(stateLabel).toHaveText("Saved");
  expect(repo.commitAttempts - attempts).toBe(1);
  expect(repo.commits - commits).toBe(1);

  // A stale-version submission is rejected: another writer moved the branch, so this tab's write is refused, nothing
  // of it is written, its changes are kept, and the page says so instead of claiming it was saved.
  await observe();
  repo.externalWrite();
  const staleHead = repo.head;
  const keptVersion = await version(page).textContent();
  await indicator.getByRole("button", { name: "Save", exact: true }).click();
  await expect(stateLabel).toHaveText("Conflict");
  expect(repo.head).toBe(staleHead);
  await expect(indicator).toContainText("Nothing of this tab's change was written");
  await expect(version(page)).toHaveText(keptVersion);
});
