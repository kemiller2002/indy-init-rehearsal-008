# T21 — integration checkpoint 2: live write path (evidence)

Task: WI-R8-T21 (integration). T0 = 2026-11-01T17:31:24Z.
Live workflow run: https://github.com/kemiller2002/indy-init-rehearsal-008/actions/runs/41340579555
(workflow_dispatch of `.github/workflows/live.yml` on `r8/t21`, source SHA `bc039273d1d59e6ec7f3893305f9c9d12796bd26`,
provider mode live-github, repository `kemiller2002/causa-write-test`, secrets CAUSA_WRITE_TOKEN / CAUSA_GITHUB_TOKEN; each
test class on its own fresh `probe/...-41340579555-<ms>` branch). Completed 19:13:42Z (T+102 min).

## Verdict: T21 completion NOT met

PAT-02, PAT-03, PAT-04 and PAT-10 do **not** pass live. Every live scenario that goes through the application's open/create path
fails before its first commit, with one root cause (a persistence defect, CR-T21-01 below):

```
System.Exception : LiveProvider "kemiller2002/causa-write-test": expected an empty head, got OpenRefused
  (NotFound "Could not resolve file for path 'investigations/<id>/manifest.json'.; Could not resolve file for path
   'investigations/<id>/events.ndjson'.; Could not resolve file for path 'investigations/<id>/snapshot.json'.")
```

Real GitHub answers `Commit.file(path:)` for an absent path with a GraphQL `NOT_FOUND` error; `classify` (Classify.fs, the
`gqlType "NOT_FOUND"` rule) turns that into `OpenRefused(NotFound ...)`, so `interpretOpen` never returns `AuthorizedEmpty` and
no investigation can be created live. The stateful fake answers the same query with null files, so every fake half passes:
a fake/real divergence of the kind PF-013 requires tests to detect (and they now do).

## Results per test (run 41340579555): 14 passed, 29 failed

| Test | Fake half | Live |
|---|---|---|
| PERSIST-041-O3 (PAT-02, three tabs at once) | pass | FAIL (open of empty investigation: OpenRefused NotFound) |
| PERSIST-042-O1, O2, O5 (PAT-03) | pass | FAIL (same cause) |
| PERSIST-042-O3 (PAT-03 injected auth/permission/rate/validation/network) | pass (fake only by design) | — |
| PERSIST-042-O4 (PAT-03 live only) | — | FAIL (same cause) |
| PERSIST-043-O1, O2, O3, O5 (PAT-04) | pass | FAIL (same cause) |
| PERSIST-043-O4 (PAT-04 live only) | — | FAIL (same cause) |
| PERSIST-005-O1..O5 (O5 = PAT-10 at provider level), PERSIST-006-O1..O5,O7, PERSIST-007-O1..O4, PERSIST-021-O1..O4 (T20, persistence) | pass | FAIL (same cause) |
| PERSIST-005 "Connect and read never mutate", PERSIST-006-O6, PERSIST-007 ReadBackOfFileOver1MB | pass | pass |
| PERSIST-008-O1..O4 (O4 live stale head) | pass | pass |
| PERSIST-025-O1..O5 (provider conformance, O3/O5 live) | pass | pass |
| INV22.ThreeSimultaneousCommits (live) | — | pass |

Browser live specs `tests/browser/live/PERSIST-041.spec.js` / `PERSIST-049.spec.js` (SU-BROWSER-LIVE, deployed-live) were not
written: they need a deployed build talking to GitHub, and the open path they would drive is blocked by CR-T21-01.

## Real commits created on kemiller2002/causa-write-test in this run

Provider-level (Execute + createCommitOnBranch, not the SM-SAVE path), from the PERSIST-025 conformance scenario as printed in the
run log (GitHub's own `createCommitOnBranch` answers):

- `779da3c5bfce7a6151aa5407766ec4ad3d8a2724` — first commit ACCEPTED; a second commit on the same expected head was refused
  GH-STALE-HEAD; head after = this commit; compare(h0, first) = ahead.
- `a8b56607dc4ec99c493a958c43e3908c6dd9e03b` — same scenario, second live conformance case; same outcome.

INV22 (live) also committed (race-writer-2 ACCEPTED, writers 1 and 3 GH-STALE-HEAD); it prints classes, not SHAs.
No commit was created by the T21 PAT suites live (each failed at the open of an empty investigation, before any mutation).

## Times relative to T0

- First verified live commit (provider level, PERSIST-025): run completed 19:13:42Z = **T+102 min**.
- First verified live stale-head refusal (provider level, PERSIST-025 / PERSIST-008-O4 / INV22): same run, **T+102 min**.
- First verified live save through the application save path (SM-SAVE) and first verified live conflict through it: **not yet**
  (blocked by CR-T21-01).

## Change requests

- **CR-T21-01 (persistence, P0; blocks T21, PF-013, PF-014, PERSIST-005/006/007/021/041/042/043/049 live).**
  Files: `src/Causa.Persistence/Classify.fs` (and/or `GitHubRequests.fs` fileSet query), `tests/Causa.TestSupport/StatefulFake.fs`.
  Change: for the file-set read (`Read "graphql:fileSet"`), a GraphQL `NOT_FOUND` error whose message is "Could not resolve file
  for path ..." on the `manifest`/`events`/`snapshot` fields, with `data.repository.ref.target.oid` present, means that file is
  absent (null), so `interpretOpen` yields `AuthorizedEmpty at` when all three are absent and `Incompatible` when some are.
  (Alternative: query `object(expression: "<oid>:<path>")`, which answers null for an absent path.) Make the stateful fake answer
  an absent path the way GitHub does (data with null field + `NOT_FOUND` error), and add the sanitized live recording, so the fake
  cannot diverge again (PF-013). Re-run live.yml afterwards; the T21 suites need no change.
