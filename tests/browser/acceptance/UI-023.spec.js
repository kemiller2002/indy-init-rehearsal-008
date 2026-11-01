// Owner: ui (WI-R8-UTESTS). UI-023: technical data validation is never presented as a verified finding. An
// investigation that was fetched, schema-validated, replayed and read back from GitHub (Saved) but whose findings are
// unresolved is never labelled "Verified" outside Administration. GitHub is served in memory with page.route.
import { expect, test } from "../support/fixtures.js";

const api = "https://api.github.com";
const cors = {
  "access-control-allow-origin": "*",
  "access-control-allow-headers": "authorization, accept, content-type, x-github-api-version",
  "access-control-allow-methods": "GET, POST, OPTIONS"
};
const oidOf = (n) => n.toString(16).padStart(40, "0");

/** A one-branch repository o/r: the head read, guarded createCommitOnBranch, and the read-back by commit. */
const fakeGitHub = async (page) => {
  const repo = { commits: 1, head: oidOf(1), files: new Map() };
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

const workspace = (page) => page.locator("[data-causa-workspace]");
const press = (page, name) => workspace(page).getByRole("button", { name, exact: true }).click();
const stateLabel = (page) => page.locator("[data-causa-save-indicator] [data-causa-save-state]");

/** Investigation-facing text: the whole page except the Administration screen. */
const investigationText = (page) =>
  page.evaluate(() => {
    const clone = document.body.cloneNode(true);
    clone.querySelector('[aria-labelledby="causa-admin-heading"]')?.remove();
    return clone.innerText ?? clone.textContent;
  });

/** Connect, create, record observations and save them: the investigation now matches GitHub (read back) while its
 * findings stay unresolved (Draft, interim report, no conclusion). */
const savedWithUnresolvedFindings = async (page, causa) => {
  await fakeGitHub(page);
  await causa.open("#/investigations/new");
  const form = page.locator("[data-causa-connect-form]");
  await form.getByLabel("Owner").fill("o");
  await form.getByLabel("Repository").fill("r");
  await form.getByLabel("Token").fill("ghp_R8UTESTSui023tokenNotShown");
  await form.getByRole("button", { name: "Connect" }).click();
  await expect(page.locator("[data-causa-connect-status]")).toHaveText("Connected to GitHub (read and write)");
  await page.locator("[data-causa-investigation-form]").getByRole("button", { name: "Create", exact: true }).click();
  await expect(stateLabel(page)).toHaveText("Saved");
  const version = workspace(page).locator("[data-causa-version]");
  for (const name of ["Record an observation", "Record an observation"]) {
    const before = Number(await version.textContent());
    await press(page, name);
    await expect(version).toHaveText(String(before + 1));
  }
  await page.locator("[data-causa-save-indicator]").getByRole("button", { name: "Save", exact: true }).click();
  await expect(stateLabel(page)).toHaveText("Saved");
  await expect(workspace(page).locator("[data-causa-lifecycle]")).not.toHaveText("Resolved");
  await expect(workspace(page).locator('[data-causa-region="report"]')).toContainText("No conclusion is verified");
};

test("UI-023-O1", async ({ page, causa }) => {
  await savedWithUnresolvedFindings(page, causa);
  // Fetched, validated, replayed and read back from GitHub: still never "Verified" (the domain asserts no
  // investigation-level verification). The save state says it matches GitHub, which is not a finding.
  const text = await investigationText(page);
  expect(text).not.toMatch(/\bVerified\b/);
  expect(text).not.toMatch(/\b(verified finding|findings? verified|investigation verified)\b/i);
  await expect(workspace(page).locator("[data-causa-lifecycle]")).not.toHaveText(/verif/i);
  for (const lozenge of await page.locator(".ef-status-lozenge").allInnerTexts()) expect(lozenge).not.toMatch(/verif/i);
});
