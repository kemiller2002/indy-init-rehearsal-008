// Owner: ui (WI-R8-UIP0). UI-010: empty, error and recovery states.
import { expect, test } from "../support/fixtures.js";

const unloaded = "#/investigations/0f8fad5b-d9cb-469f-a165-70867728950e?repo=kemiller2002/causa-data";

test("UI-010-O1", async ({ page, causa }) => {
  // No investigations: a specific empty state with a next step.
  await causa.open("#/");
  const main = page.getByRole("main");
  await expect(main).toContainText("No investigation is open in this tab");
  await expect(main.getByRole("button", { name: "Start an investigation" }).or(main.getByRole("link", { name: "Start an investigation" }))).toBeVisible();
  // No evidence: the evidence region says so instead of showing nothing.
  await causa.open("#/investigations/new");
  await expect(page.locator("[data-causa-workspace]")).toContainText("No evidence recorded yet.");
  // An address that names nothing: a recoverable state with a way back.
  await causa.open("#/nonsense");
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Page not found");
  await page.getByRole("link", { name: "Go to Investigations" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Investigations");
});

test("UI-010-O2", async ({ page, causa }) => {
  // An investigation that cannot be shown says what happened, what is preserved and a safe next step. Unsaved changes
  // live only in this tab's memory, and the page says so (PERSIST-020).
  await causa.open("#/investigations/new");
  await page.locator("[data-causa-workspace]").getByRole("button", { name: "Start investigation", exact: true }).click();
  await expect(page.getByRole("main")).toContainText("exists only in this tab");
  await causa.open(unloaded);
  const main = page.getByRole("main");
  await expect(main).toContainText("This investigation has not been loaded in this tab.");
  // The safe next step: an action that loads it (or connects the repository that holds it).
  await expect(main.getByRole("button", { name: /Connect|Open|Load/ }).or(main.getByRole("link", { name: /Connect|Open|Load/ }))).toBeVisible();
});

test("UI-010-O5", async ({ page, causa }) => {
  // No internal secret on any routine page: no token-shaped string, no Authorization detail.
  for (const route of ["#/", "#/investigations/new", unloaded, "#/administration", "#/settings", "#/nonsense"]) {
    await causa.open(route);
    const html = await page.content();
    expect(html, route).not.toMatch(/gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|Bearer\s+[A-Za-z0-9._-]{10,}/);
  }
});

// ---- WI-R8-FINALUI (ui): O3, Retry only when it performs a real action. GitHub is served in memory with page.route
// (the same one-branch repository o/r as UI-023), with a switch that makes the commit mutation fail.
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
  return repo;
};

test("UI-010-O3", async ({ page, causa }) => {
  const repo = await fakeGitHub(page);
  const ws = page.locator("[data-causa-workspace]");
  const indicator = page.locator("[data-causa-save-indicator]");
  const action = (name) => indicator.getByRole("button", { name, exact: true });
  const stateLabel = indicator.locator("[data-causa-save-state]");
  const version = ws.locator("[data-causa-version]");
  const observe = async () => {
    const before = Number(await version.textContent());
    await ws.getByRole("button", { name: "Record an observation", exact: true }).click();
    await expect(version).toHaveText(String(before + 1));
  };

  // Not connected: changes exist only in this tab and no Retry (or Save) is offered, since neither could act.
  await causa.open("#/investigations/new");
  await ws.getByRole("button", { name: "Start investigation", exact: true }).click();
  await expect(stateLabel).toHaveText("Unsaved");
  await expect(action("Retry")).toBeHidden();
  await expect(action("Save")).toBeHidden();

  // Connected and saved.
  await causa.open("#/investigations/new");
  const form = page.locator("[data-causa-connect-form]");
  await form.getByLabel("Owner").fill("o");
  await form.getByLabel("Repository").fill("r");
  await form.getByLabel("Token").fill("ghp_R8FINALUIui010tokenNotShown");
  await form.getByRole("button", { name: "Connect" }).click();
  await expect(page.locator("[data-causa-connect-status]")).toHaveText("Connected to GitHub (read and write)");
  await page.locator("[data-causa-investigation-form]").getByRole("button", { name: "Create", exact: true }).click();
  await expect(stateLabel).toHaveText("Saved");
  await expect(action("Retry")).toBeHidden();

  // A transient failure (GitHub's rate limit refuses the commit; nothing was written): Retry is offered, and pressing
  // it really sends the commit again, which now succeeds.
  await observe();
  repo.fail = "rate";
  const attemptsBeforeSave = repo.commitAttempts;
  await action("Save").click();
  await expect(action("Retry")).toBeVisible();
  await expect(stateLabel).not.toHaveText("Saved");
  expect(repo.commitAttempts).toBeGreaterThan(attemptsBeforeSave);
  repo.fail = null;
  const attemptsBeforeRetry = repo.commitAttempts;
  const headBeforeRetry = repo.head;
  await action("Retry").click();
  await expect(stateLabel).toHaveText("Saved");
  expect(repo.commitAttempts).toBeGreaterThan(attemptsBeforeRetry);
  expect(repo.head).not.toBe(headBeforeRetry);
  await expect(action("Retry")).toBeHidden();

  // A failure a retry cannot fix (the credential is refused): no Retry; Reconnect instead.
  await observe();
  repo.fail = "auth";
  await action("Save").click();
  await expect(action("Reconnect")).toBeVisible();
  await expect(action("Retry")).toBeHidden();
});
