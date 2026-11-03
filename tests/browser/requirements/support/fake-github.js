// Owner: integration. The in-memory GitHub of tests/browser/contract/connect-save-flow.spec.js, extracted verbatim for
// the requirement probes (tests/browser/requirements). Commits apply only at the expected head; files are readable back.

export const OWNER = "o";
export const REPO = "r";
export const TOKEN = "ghp_RequirementProbeTokenNeverShown01";
const api = "https://api.github.com";

const oidOf = (n) => n.toString(16).padStart(40, "0");
const cors = {
  "access-control-allow-origin": "*",
  "access-control-allow-headers":
    "authorization, accept, content-type, x-github-api-version",
  "access-control-allow-methods": "GET, POST, OPTIONS",
  "access-control-expose-headers":
    "x-ratelimit-remaining, x-ratelimit-reset, retry-after, x-github-request-id",
};

/**
 * A GitHub repository in memory: one branch, its files and head. Commits apply only at the expected head (the
 * createCommitOnBranch guard). `hold(predicate)` parks the next read (GraphQL file set or contents) matching it until released, so a test can
 * observe the page before the read-back answers.
 */
export const fakeGitHub = async (
  page,
  { push, readBackFails = false, loseCommitAnswer = () => false },
) => {
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
    route.fulfill({
      status,
      headers: { ...cors, "content-type": "application/json" },
      body: JSON.stringify(body),
    });

  const blob = (path) =>
    repo.files.has(path)
      ? { object: { text: repo.files.get(path), isTruncated: false } }
      : null;

  const fileSet = (variables) => ({
    oid: repo.head,
    manifest: blob(variables.manifest),
    events: blob(variables.events),
    snapshot: blob(variables.snapshot),
  });

  await page.route(`${api}/**`, async (route) => {
    const request = route.request();
    if (request.method() === "OPTIONS")
      return route.fulfill({ status: 204, headers: cors });
    const url = new URL(request.url());
    const body = request.postData() ? JSON.parse(request.postData()) : null;
    const entry = {
      method: request.method(),
      path: url.pathname,
      query: body?.query ?? "",
    };
    log.push(entry);
    const isRead =
      (request.method() === "POST" &&
        url.pathname === "/graphql" &&
        !entry.query.includes("createCommitOnBranch")) ||
      (request.method() === "GET" && url.pathname.includes("/contents/"));
    const held = isRead
      ? holds.find((h) => !h.taken && h.predicate(entry))
      : undefined;
    if (held) {
      held.taken = true;
      held.reached();
      await held.released;
    }

    if (
      request.method() === "GET" &&
      url.pathname === `/repos/${OWNER}/${REPO}`
    ) {
      return json(route, {
        full_name: `${OWNER}/${REPO}`,
        default_branch: "main",
        permissions: {
          admin: false,
          maintain: false,
          push,
          triage: false,
          pull: true,
        },
      });
    }
    if (request.method() === "POST" && url.pathname === "/graphql") {
      const variables = body.variables ?? {};
      if (entry.query.includes("createCommitOnBranch")) {
        const input = variables.input;
        if (input.expectedHeadOid !== repo.head) {
          return json(route, {
            data: { createCommitOnBranch: null },
            errors: [
              {
                type: "STALE_DATA",
                message: `Expected branch to point to ${input.expectedHeadOid} but it did not`,
              },
            ],
          });
        }
        for (const addition of input.fileChanges.additions ?? []) {
          repo.files.set(
            addition.path,
            Buffer.from(addition.contents, "base64").toString("utf8"),
          );
        }
        const message = [input.message?.headline, input.message?.body]
          .filter(Boolean)
          .join("\n\n");
        entry.commit = record(message);
        // The commit applied, but its answer never reaches the page (the connection dropped after GitHub had it).
        if (loseCommitAnswer(entry)) return route.abort("connectionreset");
        return json(route, {
          data: { createCommitOnBranch: { commit: { oid: repo.head } } },
        });
      }
      const target = fileSet(variables);
      entry.answeredAt = repo.head;
      const data =
        variables.oid !== undefined
          ? { repository: { object: target } }
          : { repository: { ref: { target } } };
      return json(route, { data });
    }
    if (
      request.method() === "GET" &&
      url.pathname === `/repos/${OWNER}/${REPO}/git/ref/heads/main`
    ) {
      entry.refRead = repo.head;
      return json(route, {
        ref: "refs/heads/main",
        object: { type: "commit", sha: repo.head },
      });
    }
    const compare = `/repos/${OWNER}/${REPO}/compare/`;
    if (request.method() === "GET" && url.pathname.startsWith(compare)) {
      const [base, head] = decodeURIComponent(
        url.pathname.slice(compare.length),
      ).split("...");
      const from = history.findIndex((c) => c.sha === base);
      const to = history.findIndex((c) => c.sha === head);
      if (from < 0 || to < 0) return json(route, { message: "Not Found" }, 404);
      const commits = history.slice(from + 1, to + 1);
      const before = trees.get(base);
      const after = trees.get(head);
      const names = new Set([...before.keys(), ...after.keys()]);
      const files = [...names]
        .filter((n) => before.get(n) !== after.get(n))
        .map((filename) => ({ filename }));
      return json(route, {
        status: commits.length === 0 ? "identical" : "ahead",
        ahead_by: commits.length,
        behind_by: 0,
        total_commits: commits.length,
        commits: commits.map((c) => ({
          sha: c.sha,
          commit: { message: c.message },
        })),
        files,
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
      return json(route, {
        type: "file",
        path,
        sha: oidOf(path.length),
        encoding: "base64",
        content,
      });
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
    },
  };
};
