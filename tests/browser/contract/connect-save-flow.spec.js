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
const fakeGitHub = async (page, { push, readBackFails = false }) => {
  const repo = { head: oidOf(1), commits: 1, files: new Map() };
  const trees = new Map([[repo.head, new Map()]]);
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
        repo.commits += 1;
        repo.head = oidOf(repo.commits);
        trees.set(repo.head, new Map(repo.files));
        entry.commit = repo.head;
        return json(route, { data: { createCommitOnBranch: { commit: { oid: repo.head } } } });
      }
      const target = fileSet(variables);
      entry.answeredAt = repo.head;
      const data = variables.oid !== undefined ? { repository: { object: target } } : { repository: { ref: { target } } };
      return json(route, { data });
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
const connection = (page) => page.locator("p[data-causa-connection]");
const header = (page) => page.locator("span[data-causa-connection]");

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
