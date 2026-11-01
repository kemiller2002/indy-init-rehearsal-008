# Live provider evidence (real GitHub, CI secrets; values never read by agents)

Repositories: data `kemiller2002/causa-data` (CAUSA_GITHUB_TOKEN, read-only); write `kemiller2002/causa-write-test` (CAUSA_WRITE_TOKEN, contents write there only). Workflow: `.github/workflows/live.yml`.

## Timeline
- Until 19:05Z, live.yml never ran a test: `dotnet test` was given two projects (MSB1008). Fixed in PR #16 (1a2538b).
- 19:13Z (T+102), run 41340579555 (r8/t21): 14 passed, 29 failed. Every failure had one cause: creating an investigation live was refused (`OpenRefused NotFound` on a missing path, GitHub GraphQL NOT_FOUND). The raw provider level passed: PERSIST-025 created live commits `779da3c5bfce7a6151aa5407766ec4ad3d8a2724` and `a8b56607dc4ec99c493a958c43e3908c6dd9e03b`, and the second commit on the same head was refused as a stale head.
- Fix CR-T21-01 (WI-R8-CRT2101, PR #23): a missing path in a found commit is an absent file (read context only; partial sets still fail closed). The fake now answers as GitHub does (PF-013).
- Run 41341530987 (r8/crt2101): **succeeded**. Acceptance live 34/34 (PERSIST-005 O1–O5, 006 O1–O5/O7, 007 O1–O4, 021 O1–O4, 008, 025), INV22 1/1. Correction (review T36 F-04): this run did not include the PAT suites PERSIST-041/042/043. Only run 41341841238 below proves them live. Conformance probe commits `a007e2ac1e6ae807df837e85ed67f4b678f9d335`, `aedd521da4ee9d8b7093eff7e7dcfc72725f108f`.
- Run 41341841238 (fix + T21's PAT suites, throwaway branch r8/crt2101-t21-probe): **succeeded**. 45/45 plus INV22 1/1, with provider=live-github on causa-write-test:
  - PAT-02 / PERSIST-041-O3 (three tabs save three investigations at once): `8e508f200d0deddf8f7586c5b40bc9609aee2e10`, `086f1fc489472d79251aba34f802ab9c17fee680`, `a6f6fdf36db7492a614358c31988e461093c20f2`
  - PAT-03 / PERSIST-042-O1..O5: e.g. O1 other-writer head-after `eded98776e2e5b777ccd8b40f53faed3c45d4715`, O2 `1ce66f5271bddc4536c18c0fb7ef8286da351873`, O4 `54e8abf65fe37741a7f64390f737de08c543037b` / `ffbeda694f43c5291d98c6684c9f2cb9a93b2fa9`
  - PAT-04 / PERSIST-043-O1..O5: x-confirmed `664e8dcf84b6fd8d057f811846919a572ad54c24`, `fe2826edc8a318dca0f5996a4983c1f284710ba0`, `884a4825a3760dc5f62802accaf2d9c5c697adbc`, `177f0e7866666b081abac5806c28141b7ce65013`, `a1473fe7ec43f12dc3c56460ac1fd37ec895c48e`
- **On a main SHA (review T36 F-02):** live run https://github.com/kemiller2002/indy-init-rehearsal-008/actions/runs/41342880549 on main `85f70541b92cd531eb6f29a571be3f6b03a2d543` (PR #23 merge) **succeeded**. Earlier main runs (e.g. 41342268099 at 385f2a5: 16/45) failed before #23.
- Caveat: these SHAs come from CI logs. This session cannot read causa-write-test directly. The live answer shape is recorded as DocumentedForm, because the log has no raw body.
- Not yet live: the browser live specs (PERSIST-041/049.spec.js, UI-006) need the deployed site wired to Connect and save. EXT-DATA-013 catalog live runs once PR #18's suite is on main (merged 19:16Z).
