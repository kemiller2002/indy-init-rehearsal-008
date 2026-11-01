// Owner: integration (WI-R8-CRT22B, carries WI-R8-T22B). The deployed page connects to GitHub, creates an
// investigation and saves it through the F# engine (IF41-APP) and Limen's Http effect, against an in-memory GitHub
// served with page.route: GET /repos/o/r (permissions), the GraphQL file-set read, createCommitOnBranch and the
// read-back. The indicator says Saved only after the verified read-back; the token never reaches the DOM, the
// console, storage or any request other than the Authorization header sent to api.github.com.
import { expect, test } from "../support/fixtures.js";

const OWNER = "o";
const REPO = "r";
const TOKEN = "ghp_R8CRT22BtokenNeverShown9a14";
const api = "https://api.github.com";

const oidOf = (n) => n.toString(16).padStart(40, "0");
const cors = {
  "access-control-allow-origin": "*",
  "access-control-allow-headers": "authorization, accept, content-type, x-github-api-version",
  "access-control-allow-methods": "GET, POST, OPTIONS",
  "access-control-expose-headers": "x-ratelimit-remaining, x-ratelimit-reset, retry-after, x-github-request-id"
};

/**
 * A GitHub repository in memory: one branch, its files and head. Commits apply only at the expected head (the
 * createCommitOnBranch guard). `hold(predicate)` parks the next read (GraphQL file set or contents) matching it until released, so a test can
 * observe the page before the read-back answers.
 */
const fakeGitHub = async (page, { push, readBackFails = false, loseCommitAnswer = () => false }) => {
  const repo = { head: oidOf(1), commits: 1, files: new Map() };
  const trees = new Map([[repo.head, new Map()]]);
  // WI-R8-CAPS: the branch's commits in order with their messages (for the ref read and compare the save machine's
  // reconciliation and stale-head paths make).
  const history = [{ sha: repo.head, message: "initial" }];
  const record = (message) => {
    repo.commits += 1;
    repo.head = oidOf(repo.commits);
    trees.set(repo.head, new Map(repo.files));
    history.push({ sha: repo.head, message });
    return repo.head;
  };
  const log = [];
  const holds = [];

  const json = (route, body, status = 200) =>
    route.fulfill({ status, headers: { ...cors, "content-type": "application/json" }, body: JSON.stringify(body) });

  const blob = (path) => (repo.files.has(path) ? { object: { text: repo.files.get(path), isTruncated: false } } : null);

  const fileSet = (variables) => ({
    oid: repo.head,
    manifest: blob(variables.manifest),
    events: blob(variables.events),
    snapshot: blob(variables.snapshot)
  });

  await page.route(`${api}/**`, async (route) => {
    const request = route.request();
    if (request.method() === "OPTIONS") return route.fulfill({ status: 204, headers: cors });
    const url = new URL(request.url());
    const body = request.postData() ? JSON.parse(request.postData()) : null;
    const entry = { method: request.method(), path: url.pathname, query: body?.query ?? "" };
    log.push(entry);
    const isRead =
      (request.method() === "POST" && url.pathname === "/graphql" && !entry.query.includes("createCommitOnBranch")) ||
      (request.method() === "GET" && url.pathname.includes("/contents/"));
    const held = isRead ? holds.find((h) => !h.taken && h.predicate(entry)) : undefined;
    if (held) {
      held.taken = true;
      held.reached();
      await held.released;
    }

    if (request.method() === "GET" && url.pathname === `/repos/${OWNER}/${REPO}`) {
      return json(route, {
        full_name: `${OWNER}/${REPO}`,
        default_branch: "main",
        permissions: { admin: false, maintain: false, push, triage: false, pull: true }
      });
    }
    if (request.method() === "POST" && url.pathname === "/graphql") {
      const variables = body.variables ?? {};
      if (entry.query.includes("createCommitOnBranch")) {
        const input = variables.input;
        if (input.expectedHeadOid !== repo.head) {
          return json(route, {
            data: { createCommitOnBranch: null },
            errors: [{ type: "STALE_DATA", message: `Expected branch to point to ${input.expectedHeadOid} but it did not` }]
          });
        }
        for (const addition of input.fileChanges.additions ?? []) {
          repo.files.set(addition.path, Buffer.from(addition.contents, "base64").toString("utf8"));
        }
        const message = [input.message?.headline, input.message?.body].filter(Boolean).join("\n\n");
        entry.commit = record(message);
        // The commit applied, but its answer never reaches the page (the connection dropped after GitHub had it).
        if (loseCommitAnswer(entry)) return route.abort("connectionreset");
        return json(route, { data: { createCommitOnBranch: { commit: { oid: repo.head } } } });
      }
      const target = fileSet(variables);
      entry.answeredAt = repo.head;
      const data = variables.oid !== undefined ? { repository: { object: target } } : { repository: { ref: { target } } };
      return json(route, { data });
    }
    if (request.method() === "GET" && url.pathname === `/repos/${OWNER}/${REPO}/git/ref/heads/main`) {
      entry.refRead = repo.head;
      return json(route, { ref: "refs/heads/main", object: { type: "commit", sha: repo.head } });
    }
    const compare = `/repos/${OWNER}/${REPO}/compare/`;
    if (request.method() === "GET" && url.pathname.startsWith(compare)) {
      const [base, head] = decodeURIComponent(url.pathname.slice(compare.length)).split("...");
      const from = history.findIndex((c) => c.sha === base);
      const to = history.findIndex((c) => c.sha === head);
      if (from < 0 || to < 0) return json(route, { message: "Not Found" }, 404);
      const commits = history.slice(from + 1, to + 1);
      const before = trees.get(base);
      const after = trees.get(head);
      const names = new Set([...before.keys(), ...after.keys()]);
      const files = [...names].filter((n) => before.get(n) !== after.get(n)).map((filename) => ({ filename }));
      return json(route, {
        status: commits.length === 0 ? "identical" : "ahead",
        ahead_by: commits.length,
        behind_by: 0,
        total_commits: commits.length,
        commits: commits.map((c) => ({ sha: c.sha, commit: { message: c.message } })),
        files
      });
    }
    const contents = `/repos/${OWNER}/${REPO}/contents/`;
    if (request.method() === "GET" && url.pathname.startsWith(contents)) {
      // A file at a named commit (the save's read-back reads the commit it wrote).
      const path = decodeURIComponent(url.pathname.slice(contents.length));
      const at = url.searchParams.get("ref");
      entry.answeredAt = at;
      const text = readBackFails ? undefined : trees.get(at)?.get(path);
      if (text === undefined) return json(route, { message: "Not Found" }, 404);
      const content = Buffer.from(text, "utf8").toString("base64");
      return json(route, { type: "file", path, sha: oidOf(path.length), encoding: "base64", content });
    }
    return json(route, { message: "Not Found" }, 404);
  });

  return {
    repo,
    log,
    /** Another writer's commit on the branch (here: one that removes every file of the investigation). */
    externalCommit(change) {
      change(repo.files);
      return record("another writer");
    },
    refReads: () => log.filter((e) => e.refRead !== undefined),
    commits: () => log.filter((e) => e.query.includes("createCommitOnBranch")),
    reads: () => log.filter((e) => e.answeredAt !== undefined),
    hold(predicate) {
      const h = { predicate, taken: false };
      h.reaching = new Promise((resolve) => (h.reached = resolve));
      h.released = new Promise((resolve) => (h.release = resolve));
      holds.push(h);
      return h;
    }
  };
};

/** Everything the page could leak the token through: console, requests (except the one header), storage, DOM. */
const watchToken = (page) => {
  const leaks = [];
  page.on("console", (message) => {
    if (message.text().includes(TOKEN)) leaks.push(`console: ${message.type()}`);
  });
  page.on("request", (request) => {
    const url = request.url();
    if (url.includes(TOKEN)) leaks.push(`url: ${url}`);
    if ((request.postData() ?? "").includes(TOKEN)) leaks.push(`body: ${url}`);
    for (const [name, value] of Object.entries(request.headers())) {
      if (!value.includes(TOKEN)) continue;
      const allowed = name.toLowerCase() === "authorization" && url.startsWith(`${api}/`);
      if (!allowed) leaks.push(`header ${name}: ${url}`);
    }
  });
  return {
    leaks,
    async checkPage() {
      const found = await page.evaluate((token) => {
        const places = [];
        if (document.documentElement.outerHTML.includes(token)) places.push("DOM markup");
        if ((document.body.innerText ?? "").includes(token)) places.push("DOM text");
        for (const store of [localStorage, sessionStorage]) {
          for (let i = 0; i < store.length; i += 1) {
            const key = store.key(i);
            if (`${key}${store.getItem(key)}`.includes(token)) places.push("web storage");
          }
        }
        if (document.cookie.includes(token)) places.push("cookie");
        return places;
      }, TOKEN);
      leaks.push(...found);
    }
  };
};

const connect = async (page) => {
  const form = page.locator("[data-causa-connect-form]");
  await form.getByLabel("Owner").fill(OWNER);
  await form.getByLabel("Repository").fill(REPO);
  await form.getByLabel("Token").fill(TOKEN);
  await form.getByRole("button", { name: "Connect" }).click();
};

const indicator = (page) => page.locator("[data-causa-save-indicator]");
const stateLabel = (page) => indicator(page).locator("[data-causa-save-state]");
const connection = (page) => page.locator("[data-causa-connect-status]");
const header = (page) => page.locator("[data-causa-connection]");

test("Connect, Create and Save reach GitHub; Saved only after the read-back; the token never leaks", async ({ page, causa, failures }) => {
  const github = await fakeGitHub(page, { push: true });
  const token = watchToken(page);
  const authorizations = [];
  page.on("request", (request) => {
    if (request.url().startsWith(`${api}/`) && request.method() !== "OPTIONS") authorizations.push(request.headers().authorization ?? "");
  });

  await causa.open("#/investigations/new");
  await connect(page);
  await expect(connection(page)).toHaveText("Connected to GitHub (read and write)");
  await expect(header(page)).toHaveText("Connected to GitHub (read and write)");
  await expect(page.locator("[data-causa-connect-form]")).toBeHidden();
  await token.checkPage();

  // Create: the head read finds nothing, the create commit is sent, and the investigation is Saved only once the
  // read-back after that commit has verified it.
  const createReadBack = github.hold((e) => github.commits().length === 1);
  await page.locator("[data-causa-investigation-form]").getByRole("button", { name: "Create", exact: true }).click();
  await createReadBack.reaching;
  expect(github.commits()).toHaveLength(1);
  await expect(stateLabel(page)).not.toHaveText("Saved");
  createReadBack.release();
  await expect(stateLabel(page)).toHaveText("Saved");
  await expect(page.locator("[data-causa-lifecycle]")).not.toHaveText("No investigation");
  const created = github.commits()[0].commit;
  expect(github.reads().at(-1).answeredAt).toBe(created);
  const createdFiles = [...github.repo.files.keys()];
  expect(createdFiles).toHaveLength(3);

  // A change makes it Unsaved; Save sends one guarded commit and Saved follows only the read-back.
  await page.locator("[data-causa-workspace]").getByRole("button", { name: "Record an observation", exact: true }).click();
  await expect(stateLabel(page)).toHaveText("Unsaved");
  const observations = page.locator('[data-causa-workspace] [data-text="observationCount"]');
  await expect(observations).toHaveText("1");
  const save = indicator(page).getByRole("button", { name: "Save", exact: true });
  await expect(save).toBeVisible();
  const saveReadBack = github.hold(() => github.commits().length === 2);
  await save.click();
  await saveReadBack.reaching;
  expect(github.commits()).toHaveLength(2);
  await expect(stateLabel(page)).not.toHaveText("Saved");
  saveReadBack.release();
  await expect(stateLabel(page)).toHaveText("Saved");
  expect(github.reads().at(-1).answeredAt).toBe(github.commits()[1].commit);
  await expect(observations).toHaveText("1");
  expect([...github.repo.files.keys()].sort()).toEqual(createdFiles.sort());

  // The credential travelled only as the Authorization header to api.github.com, on every request.
  expect(authorizations.length).toBeGreaterThanOrEqual(5);
  for (const value of authorizations) expect(value.endsWith(` ${TOKEN}`)).toBe(true);
  await token.checkPage();
  expect(token.leaks).toEqual([]);
  expect(failures).toEqual([]);
});

test("a credential without push is connected read-only: Create sends no commit and no Save is offered", async ({ page, causa, failures }) => {
  const github = await fakeGitHub(page, { push: false });
  const token = watchToken(page);

  await causa.open("#/investigations/new");
  await connect(page);
  await expect(connection(page)).toHaveText("Connected to GitHub (read only)");
  await page.locator("[data-causa-investigation-form]").getByRole("button", { name: "Create", exact: true }).click();
  await expect(page.locator("[data-causa-investigation-notice]")).toHaveText("This credential cannot write to the repository.");
  await expect(stateLabel(page)).not.toHaveText("Saved");
  await expect(indicator(page).getByRole("button", { name: "Save", exact: true })).toBeHidden();
  expect(github.commits()).toEqual([]);
  expect(github.repo.files.size).toBe(0);
  await token.checkPage();
  expect(token.leaks).toEqual([]);
  expect(failures).toEqual([]);
});

test("a save whose read-back cannot confirm the commit is never shown as Saved", async ({ page, causa }) => {
  const github = await fakeGitHub(page, { push: true, readBackFails: true });
  await causa.open("#/investigations/new");
  await connect(page);
  await page.locator("[data-causa-investigation-form]").getByRole("button", { name: "Create", exact: true }).click();
  await expect(stateLabel(page)).toHaveText("Saved");
  await page.locator("[data-causa-workspace]").getByRole("button", { name: "Record an observation", exact: true }).click();
  await indicator(page).getByRole("button", { name: "Save", exact: true }).click();
  await expect.poll(() => github.commits().length).toBe(2);
  // The commit was answered, but its read-back did not verify it: the outcome stays unknown, never Saved.
  await expect(stateLabel(page)).toHaveText("Unknown commit outcome");
  await expect(stateLabel(page)).not.toHaveText("Saved");
});

// WI-R8-CAPS: Limen's schedule capability runs the engine's Timer effects. A commit whose answer is lost leaves the
// outcome unknown; the save machine's automatic reconciliation read fires on its schedule (2 s, PERSIST-016) with no
// user action, finds this tab's marked commit, verifies its bytes and only then says Saved.
test("an unknown commit outcome is reconciled automatically on the schedule and resolves to Saved", async ({ page, causa }) => {
  let lose = false;
  const github = await fakeGitHub(page, { push: true, loseCommitAnswer: () => lose });
  await causa.open("#/investigations/new");
  await connect(page);
  await page.locator("[data-causa-investigation-form]").getByRole("button", { name: "Create", exact: true }).click();
  await expect(stateLabel(page)).toHaveText("Saved");
  await page.locator("[data-causa-workspace]").getByRole("button", { name: "Record an observation", exact: true }).click();
  lose = true;
  await indicator(page).getByRole("button", { name: "Save", exact: true }).click();
  await expect.poll(() => github.commits().length).toBe(2);
  lose = false;
  const written = github.commits()[1].commit;
  await expect(stateLabel(page)).toHaveText("Unknown commit outcome");
  expect(github.refReads()).toEqual([]);
  // Nobody clicks Check again: the scheduled read happens by itself.
  await expect.poll(() => github.refReads().length, { timeout: 15_000 }).toBeGreaterThanOrEqual(1);
  await expect(stateLabel(page)).toHaveText("Saved", { timeout: 15_000 });
  expect(github.refReads()[0].refRead).toBe(written);
  expect(github.reads().at(-1).answeredAt).toBe(written);
  // No second commit was sent: the first one had applied.
  expect(github.commits()).toHaveLength(2);
});

// WI-R8-CAPS: Limen's files capability runs the engine's Download effect. A save that meets another writer's commit
// which removed the investigation cannot be resolved here; Export my changes hands the browser a file holding the
// unsaved commands, and nothing is written to GitHub.
test("Export on an unresolvable conflict downloads the unsaved commands", async ({ page, causa, failures }) => {
  const github = await fakeGitHub(page, { push: true });
  await causa.open("#/investigations/new");
  await connect(page);
  await page.locator("[data-causa-investigation-form]").getByRole("button", { name: "Create", exact: true }).click();
  await expect(stateLabel(page)).toHaveText("Saved");
  await page.locator("[data-causa-workspace]").getByRole("button", { name: "Record an observation", exact: true }).click();
  await expect(stateLabel(page)).toHaveText("Unsaved");
  github.externalCommit((files) => files.clear());
  await indicator(page).getByRole("button", { name: "Save", exact: true }).click();
  const exportButton = page.getByRole("button", { name: "Export my changes" }).locator("visible=true").first();
  await expect(exportButton).toBeVisible({ timeout: 15_000 });
  const commitsBefore = github.commits().length;
  const [download] = await Promise.all([page.waitForEvent("download"), exportButton.click()]);
  expect(download.suggestedFilename()).toBe("causa-unsaved-commands.json");
  const chunks = [];
  for await (const chunk of await download.createReadStream()) chunks.push(chunk);
  const exported = JSON.parse(Buffer.concat(chunks).toString("utf8"));
  expect(exported.commands).toHaveLength(1);
  expect(exported.commands[0]).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/);
  await expect(page.locator("[data-causa-investigation-notice]")).toHaveText("Your unsaved changes were downloaded.");
  expect(github.commits()).toHaveLength(commitsBefore);
  expect(failures).toEqual([]);
});

// WI-R8-CAPS: once Connect has made the credential, the typed token is not left in the password field: the field
// leaves the DOM, and after Disconnect it comes back empty.
test("the token field is empty after Connect", async ({ page, causa, failures }) => {
  await fakeGitHub(page, { push: true });
  const token = watchToken(page);
  await causa.open("#/investigations/new");
  await connect(page);
  await expect(connection(page)).toHaveText("Connected to GitHub (read and write)");
  const values = () => page.evaluate(() => [...document.querySelectorAll('input[type="password"]')].map((i) => i.value));
  expect((await values()).filter((v) => v !== "")).toEqual([]);
  await token.checkPage();
  await page.locator("[data-causa-investigation-form]").getByRole("button", { name: "Disconnect" }).click();
  const field = page.locator("[data-causa-connect-form]").getByLabel("Token");
  await expect(field).toBeVisible();
  await expect(field).toHaveValue("");
  expect(token.leaks).toEqual([]);
  expect(failures).toEqual([]);
});
