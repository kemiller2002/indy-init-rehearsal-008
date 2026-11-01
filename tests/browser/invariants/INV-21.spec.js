// Owner: ui (WI-R8-UTESTS). INV-21: the credential exists only in tab memory and is sent only in the Authorization
// header over HTTPS to the GitHub API; it never appears in a URL, browser store, the DOM, the console, a request body or
// another header, nor in an artifact the page produces (PF-015-O4). GitHub is served in memory with page.route.
import { expect, test } from "../support/fixtures.js";

const TOKEN = "ghp_R8UTESTSinv21NeverShown5c27e";
const api = "https://api.github.com";
const cors = {
  "access-control-allow-origin": "*",
  "access-control-allow-headers": "authorization, accept, content-type, x-github-api-version",
  "access-control-allow-methods": "GET, POST, OPTIONS"
};

const fakeGitHub = (page) =>
  page.route(`${api}/**`, (route) => {
    const request = route.request();
    if (request.method() === "OPTIONS") return route.fulfill({ status: 204, headers: cors });
    const url = new URL(request.url());
    const json = (body, status = 200) =>
      route.fulfill({ status, headers: { ...cors, "content-type": "application/json" }, body: JSON.stringify(body) });
    if (request.method() === "GET" && url.pathname === "/repos/o/r") {
      return json({ full_name: "o/r", default_branch: "main", permissions: { admin: false, maintain: false, push: true, triage: false, pull: true } });
    }
    return json({ message: "Not Found" }, 404);
  });

/** Every place in the page a credential could rest: URL, DOM, web storage, cookies, IndexedDB, Cache Storage, OPFS. */
const placesHolding = (page) =>
  page.evaluate(async (token) => {
    const places = [];
    if (location.href.includes(token)) places.push("URL");
    if (document.documentElement.outerHTML.includes(token)) places.push("DOM markup");
    if ((document.body.innerText ?? "").includes(token)) places.push("DOM text");
    for (const input of document.querySelectorAll("input")) if (input.value.includes(token)) places.push(`input ${input.name || input.type}`);
    for (const store of [localStorage, sessionStorage]) {
      for (let i = 0; i < store.length; i += 1) {
        const key = store.key(i);
        if (`${key}${store.getItem(key)}`.includes(token)) places.push("web storage");
      }
    }
    if (document.cookie.includes(token)) places.push("cookie");
    if (indexedDB.databases && (await indexedDB.databases()).length > 0) places.push("IndexedDB");
    if ("caches" in window && (await caches.keys()).length > 0) places.push("Cache Storage");
    if (navigator.storage && navigator.storage.getDirectory) {
      // WebKit refuses OPFS in an ephemeral (Playwright) context with UnknownError: then no page can store there.
      const root = await navigator.storage.getDirectory().catch((e) => (e && e.name === "UnknownError" ? null : Promise.reject(e)));
      if (root) for await (const _ of root.keys()) { places.push("OPFS"); break; }
    }
    return places;
  }, TOKEN);

test("no credential in storage, URL or artifact", async ({ page, causa }) => {
  await fakeGitHub(page);
  const leaks = [];
  const authorized = [];
  page.on("console", (message) => {
    if (message.text().includes(TOKEN)) leaks.push(`console ${message.type()}`);
  });
  page.on("request", (request) => {
    const url = request.url();
    if (url.includes(TOKEN)) leaks.push(`url ${url}`);
    if ((request.postData() ?? "").includes(TOKEN)) leaks.push(`body ${url}`);
    for (const [name, value] of Object.entries(request.headers())) {
      if (!value.includes(TOKEN)) continue;
      if (name.toLowerCase() === "authorization" && url.startsWith(`${api}/`)) authorized.push(url);
      else leaks.push(`header ${name} ${url}`);
    }
  });
  page.on("download", (download) => leaks.push(`download ${download.suggestedFilename()}`));

  await causa.open("#/investigations/new");
  const form = page.locator("[data-causa-connect-form]");
  await form.getByLabel("Owner").fill("o");
  await form.getByLabel("Repository").fill("r");
  await form.getByLabel("Token").fill(TOKEN);
  await form.getByRole("button", { name: "Connect" }).click();
  await expect(page.locator("[data-causa-connect-status]")).toHaveText("Connected to GitHub (read and write)");
  // The credential was used, and only as the Authorization header over HTTPS to the GitHub API.
  expect(authorized.length).toBeGreaterThanOrEqual(1);
  expect(authorized.every((u) => u.startsWith("https://"))).toBe(true);
  expect(await placesHolding(page)).toEqual([]);

  // Work in the connected tab, then every screen a person can reach: none shows or stores the credential.
  const ws = page.locator("[data-causa-workspace]");
  await ws.getByRole("button", { name: "Start investigation", exact: true }).click();
  await ws.getByRole("button", { name: "Record an observation", exact: true }).click();
  expect(await placesHolding(page)).toEqual([]);
  for (const route of ["#/", "#/administration", "#/investigations/new"]) {
    await page.evaluate((hash) => { location.hash = hash; }, route);
    await expect(page.locator("html")).toHaveAttribute("data-engine", "ready");
    expect(await placesHolding(page), route).toEqual([]);
  }
  // An artifact of the page (its rendered text and an accessibility snapshot, which feed screenshots and traces).
  expect(await page.locator("body").ariaSnapshot()).not.toContain(TOKEN);

  // Disconnecting forgets it: nothing anywhere holds it afterwards.
  await page.locator("[data-causa-connect]").getByRole("button", { name: "Disconnect" }).click();
  await expect(page.locator("[data-causa-connect-status]")).toHaveText("Not connected");
  expect(await placesHolding(page)).toEqual([]);
  expect(leaks).toEqual([]);
});
