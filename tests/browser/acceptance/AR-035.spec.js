// Owner: integration (WI-R8-ITESTS2). AR-035 Multi-writer persistence semantics.
//
// AR-035-O1: persistence under concurrent writers. Two tabs of one browser profile write to the same repository
// (an in-memory GitHub served to the whole browser context with context.route, the stubbed-provider pattern of
// tests/browser/contract/connect-save-flow.spec.js): their creates and saves interleave, so each commit after the
// first is guarded on a head the other tab has moved; one save's answer is lost after GitHub applied it; and a
// reload drops that tab's memory. Every save still ends Saved only after a verified read-back, the lost answer is
// reconciled without a second commit, and every tab's commit stays on the branch.
// AR-035-O4: each tab holds its own state in memory and tabs share nothing but GitHub: a second tab of the same
// profile sees neither the first tab's connection nor its investigation, and no browser store holds either.
import { expect, test } from "../support/fixtures.js";

const OWNER = "o";
const REPO = "r";
const TOKEN = "ghp_R8ITESTS2tokenNeverStored35";
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
const fakeGitHub = async (target, { push, readBackFails = false, loseCommitAnswer = () => false }) => {
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

  await target.route(`${api}/**`, async (route) => {
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

const connect = async (page) => {
  const form = page.locator("[data-causa-connect-form]");
  await form.getByLabel("Owner").fill(OWNER);
  await form.getByLabel("Repository").fill(REPO);
  await form.getByLabel("Token").fill(TOKEN);
  await form.getByRole("button", { name: "Connect" }).click();
  await expect(page.locator("[data-causa-connect-status]")).toHaveText("Connected to GitHub (read and write)");
};

const stateLabel = (page) => page.locator("[data-causa-save-indicator] [data-causa-save-state]");
const saveButton = (page) => page.locator("[data-causa-save-indicator]").getByRole("button", { name: "Save", exact: true });
const create = (page) => page.locator("[data-causa-investigation-form]").getByRole("button", { name: "Create", exact: true }).click();
const observe = (page) =>
  page.locator("[data-causa-workspace]").getByRole("button", { name: "Record an observation", exact: true }).click();
const observations = (page) => page.locator('[data-causa-workspace] [data-text="observationCount"]');

/** Every browser store the page can reach, flattened to text (web storage, cookies, IndexedDB names, Cache Storage). */
const browserStores = (page) =>
  page.evaluate(async () => {
    const out = [];
    for (const store of [localStorage, sessionStorage]) {
      for (let i = 0; i < store.length; i += 1) out.push(`${store.key(i)}=${store.getItem(store.key(i))}`);
    }
    if (document.cookie) out.push(document.cookie);
    if (indexedDB.databases) for (const db of await indexedDB.databases()) out.push(`idb:${db.name}`);
    if (self.caches) for (const name of await caches.keys()) out.push(`cache:${name}`);
    return out.join("\n");
  });

const investigationIds = (repo) =>
  [...new Set([...repo.files.keys()].flatMap((path) => path.match(/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/g) ?? []))];

const isAncestorOfHead = (github, commit) => {
  // The fake's branch is linear: every recorded commit up to the head is reachable from it.
  const n = parseInt(commit, 16);
  return n >= 1 && n <= github.repo.commits;
};

test("AR-035-O1", async ({ context, failures }) => {
  let lose = false;
  const github = await fakeGitHub(context, { push: true, loseCommitAnswer: () => lose });
  const a = await context.newPage();
  const b = await context.newPage();
  const errors = [];
  for (const p of [a, b]) p.on("pageerror", (e) => errors.push(e.message));
  for (const p of [a, b]) {
    await p.goto("/dist/index.html#/investigations/new");
    await expect(p.locator("html")).toHaveAttribute("data-engine", "ready", { timeout: 30_000 });
    await connect(p);
  }

  // Interleaved creates: B's create is guarded on the head A's create moved.
  await create(a);
  await expect(stateLabel(a)).toHaveText("Saved");
  await create(b);
  await expect(stateLabel(b)).toHaveText("Saved");
  expect(github.commits()).toHaveLength(2);
  expect(investigationIds(github.repo)).toHaveLength(2);

  // Both tabs change their investigation; A saves, then B saves on a head A has moved.
  await observe(a);
  await observe(b);
  await expect(stateLabel(a)).toHaveText("Unsaved");
  await expect(stateLabel(b)).toHaveText("Unsaved");
  await saveButton(a).click();
  await expect(stateLabel(a)).toHaveText("Saved");
  await saveButton(b).click();
  await expect(stateLabel(b)).toHaveText("Saved", { timeout: 15_000 });
  await expect(observations(a)).toHaveText("1");
  await expect(observations(b)).toHaveText("1");

  // A lost answer: A's next commit applies but its answer never arrives. A must not claim Saved before reconciling,
  // and must not write the same change twice.
  await observe(a);
  const applied = () => github.commits().filter((c) => c.commit).length;
  const appliedBeforeLoss = applied();
  lose = true;
  await saveButton(a).click();
  await expect.poll(applied).toBe(appliedBeforeLoss + 1);
  lose = false;
  await expect(stateLabel(a)).toHaveText("Unknown commit outcome");
  await expect(stateLabel(a)).toHaveText("Saved", { timeout: 15_000 });
  // Reconciliation found the commit that applied: no second commit was written for the same change.
  expect(applied()).toBe(appliedBeforeLoss + 1);

  // A reload drops B's memory: it starts with no investigation and no connection; GitHub keeps every commit.
  await b.reload();
  await expect(b.locator("html")).toHaveAttribute("data-engine", "ready", { timeout: 30_000 });
  await expect(b.locator("[data-causa-lifecycle]")).toHaveText("No investigation");
  for (const c of github.commits().filter((c) => c.commit)) expect(isAncestorOfHead(github, c.commit)).toBe(true);
  expect(investigationIds(github.repo)).toHaveLength(2);
  expect(errors).toEqual([]);
  expect(failures).toEqual([]);
});

test("AR-035-O4", async ({ context, failures }) => {
  const github = await fakeGitHub(context, { push: true });
  const a = await context.newPage();
  await a.goto("/dist/index.html#/investigations/new");
  await expect(a.locator("html")).toHaveAttribute("data-engine", "ready", { timeout: 30_000 });
  await connect(a);
  await create(a);
  await expect(stateLabel(a)).toHaveText("Saved");
  await observe(a);
  await expect(stateLabel(a)).toHaveText("Unsaved");
  await expect(observations(a)).toHaveText("1");
  const [id] = investigationIds(github.repo);
  expect(id).toBeTruthy();

  // A second tab of the same profile: nothing of A's connection or investigation reaches it.
  const b = await context.newPage();
  await b.goto("/dist/index.html#/investigations/new");
  await expect(b.locator("html")).toHaveAttribute("data-engine", "ready", { timeout: 30_000 });
  await expect(b.locator("[data-causa-connect-form]")).toBeVisible();
  await expect(b.locator("[data-causa-connect-status]")).not.toHaveText(/Connected to GitHub/);
  await expect(b.locator("[data-causa-lifecycle]")).toHaveText("No investigation");
  expect(await b.content()).not.toContain(id);

  // A is unchanged by B's start, and neither tab put the investigation or the credential in a browser store.
  await expect(stateLabel(a)).toHaveText("Unsaved");
  await expect(observations(a)).toHaveText("1");
  for (const p of [a, b]) {
    const stores = await browserStores(p);
    expect(stores).not.toContain(id);
    expect(stores).not.toContain(TOKEN);
  }
  expect(github.commits()).toHaveLength(1);
  expect(failures).toEqual([]);
});
