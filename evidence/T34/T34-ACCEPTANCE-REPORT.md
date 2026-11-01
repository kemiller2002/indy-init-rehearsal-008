# T34 final acceptance: preservation gate, ledger, computed verdict, measurements

- Work item: WI-R8-T34 (integration lane). Generated 2026-11-01 about 23:15Z.
- **Candidate: `c61bfe24751572dadf8110031e78d90a8d31dce0`** (main, merge of PR #52 WI-R8-T32L). The coordinator named it the final candidate, and code is frozen at it.
- Plan: v4 `VP-1e3c66bcc958` (613 obligations, all P0, 0 P1), run through `requirements-compiled/v4.1.1/verification-execution-plan.json` (11 suites, 660 plan tests plus 140 own tests).
- Method: T31's method (`evidence/RC/raw/`), regenerated at the candidate. Results come only from CI runs on the candidate: the TRX artifacts for .NET and the Playwright list reporter in the job logs for the browser suites. An obligation counts as verified only when every test identity bound to it in the plan passed at the candidate SHA. Nothing was estimated and no suite was run locally.
- Files: `evidence/T34/T34-matrix.json` (one row per obligation), `evidence/T34/ledger.json` (`causa.acceptance-ledger/2`), `evidence/T34/evidence-manifest.json` with `evidence/T34/acceptance-evidence/` (`causa.acceptance-evidence-manifest/1` bundle), and `evidence/T34/raw/` (scripts, parsed CI results, gate run, deployment checks, verdict output).

## Computed verdict

<!-- acceptance-verdict: INVALID -->

`tools/compute-acceptance-verdict.py` computed **INVALID** (exit 3). This is the only verdict in this report, and it comes from the tool. **The candidate cannot be released.**

The tool returns INVALID because the trust inputs fail before it compares the ledger with them:
1. **Approvals file.** The pinned approvals file (`kickoff/rehearsal-007/approvals.causa.json`, the latest one, which holds APR-V411-OWNER-1/2) fails the verifier's trust checks:
   - No approval names an immutable authority source (`source.repository/commit/path`).
   - The changes do not use the `requirement_id` and `action` identity the verifier expects.
   
   No agent may write approvals, so only the owner can fix this.
2. **Ledger states.** If the trust inputs were valid, the ledger would still yield FAILED:
   - G3, G7 and G8 are FAIL.
   - G0 and G2 are BLOCKED.
   - G1 and G4 to G6 are NOT_RUN.
   - 108 P0 requirements are NOT_RUN and 3 are BLOCKED.
   
   Only G9 (deployment) is PASS.

Inputs passed to the tool (exact command: `evidence/T34/raw/run-verdict.sh`; output: `evidence/T34/raw/verdict.json`):

| Input | Value |
|---|---|
| ledger | `evidence/T34/ledger.json` (sha256 written in `raw/verdict-inputs.txt`) |
| baseline | `requirements/causa-requirements-baseline-v4.json`, BL-CAUSA-INDY-INIT-62df28a559c0, sha256 `f86a3539e48ee8dda7d99d5f4f5546a18e2397a82d308a277c8f346edb2a8f1a`, source at `62df28a559c054226dd4022e9744cf1db06e954f` |
| approvals | `kickoff/rehearsal-007/approvals.causa.json`, sha256 `63a9ce5b8a5cd160fff7d485bbd877276625b2093e894e3b7fe75b57b9d7a8c8` |
| trusted operator id | `kemiller2002` |
| evidence manifest | `evidence/T34/evidence-manifest.json`, sha256 `cc6263b0fc798cb7e13d82e9dc93ded25aa3440875895089f0d48fce9df9fdfa` (51 PASS evidence results: 50 requirements and G9) |
| trusted source SHA | `c61bfe24751572dadf8110031e78d90a8d31dce0` |

## Counts (P0; the frozen plan has no P1 obligations)

| Status | Obligations |
|---|---|
| verified (every bound test passed at the candidate) | **256** |
| failed (a bound test failed) | **1**: PERSIST-016-O5, where `INV04.RequestBounds` fails (DEV-R8-11, owner decision) |
| not run | **356**: 353 with a bound test that has no result, plus 3 that pass everything except the missing owner-approval record (AR-008-O2, AR-024-O1, AR-024-O2) |
| **total** | **613** |

In the matrix, PERSIST-016-O5, AR-008-O2, AR-024-O1 and AR-024-O2 are annotated `unverified-by-design`: the only thing blocking each is an owner decision. The table above counts them as failed (1) and not run (3).

- Requirements whose obligations are all verified: **50 of 161** P0 (T31: 18). They are AR-001/002/003/004/006/007/009/010/011/013/018/019/020/022/023/027/028/029/031/034, EXT-DATA-011/013, PERSIST-005/006/007/008/009/010/011/013/014/015/021/023/025/032/042/043/045/047/051, UI-014/018/019/020/029, VER-007/008 and WDL-001/002.
- Test identities: 369 pass, 1 fail, 430 not run (800).
- Change since T31 (b0ba767): verified obligations went from 113 to 256. Most of the gain comes from the T32 acceptance tests and from the T33 independent-review record, which now counts for the obligations bound to `praxis:independent-review`.

### Rules applied (T33 review)
- **PERSIST-007-O1 is verified.** T32L landed, and every bound identity passed at the candidate:
  - the deterministic `PERSIST007Tests.PERSIST-007-O1`;
  - the live `PERSIST007Tests.PERSIST-007-O1`;
  - the rewritten end-to-end live `PERSIST007Tests.ReadBackOfFileOver1MB`, which passed in live.yml run 41360391689 against real GitHub.
- **WebKit.** Any failure in the WebKit job would count as failed. The WebKit job had no failures and no retries (86 passed). INV-06 (`every store empty of investigation content [webkit]`) and INV-21 (`no credential in storage, URL or artifact [webkit]`) **passed** at the candidate, so the T33-F-08 issue fixed in #50 is green.
- **INV04 / DEV-R8-11** is an owner decision. AR-008-O2 and AR-024-O1/O2 have no owner-approval record. The T33 review record (`evidence/reviews/T33-final-review.md`) counts for `praxis:independent-review`. It ratifies no owner decision.

## Per-suite results

| Suite | Environment | Planned | Pass | Fail | Not run | Evidence |
|---|---|---|---|---|---|---|
| SU-OWN-DOTNET | ci | 137 | 113 | 0 | 24 (contract producer/consumer identities absent) | ci.yml job 125009364495 |
| SU-OWN-BROWSER | ci-browser | 3 | 3 | 0 | 0 | chromium job 125009390333 |
| SU-DOTNET-DETERMINISTIC | ci | 116 | 67 | 0 | 49 (stub or absent) | ci.yml job 125009364495 |
| SU-DOTNET-INVARIANTS | ci | 21 | 20 | **1** (INV04) | 0 | ci.yml job 125009364495, invariant step (34/35) |
| SU-BROWSER-CHROMIUM | ci-browser | 281 | 82 | 0 | 199 (spec absent or title did not run) | chromium job 125009390333 |
| SU-BROWSER-WEBKIT | ci-browser | 72 | 23 | 0 | 49 (33 spec absent, 16 title not run in CI WebKit) | webkit job 125009390527 |
| SU-DOTNET-LIVE | live-github | 141 | 60 | 0 | 81 (stub files) | live.yml job 125009663660 |
| SU-BROWSER-LIVE | deployed-live | 17 | 0 | 0 | 17 (`tests/browser/live` absent) | none |
| SU-VERIFIER | independent | 2 | 0 | 0 | 2 (no verifier job) | none |
| SU-REVIEW-RECORDS | process | 2 | 1 | 0 | 1 (owner approval) | `evidence/reviews/T33-final-review.md` |
| SU-PROCESS-RECORDS | process | 8 | 0 | 0 | 8 (no Praxis evidence of these kinds) | none |

The CI totals at the candidate:
- ci.yml non-live .NET: 264 of 264 passed.
- Invariants: 34 of 35 passed (INV04 failed).
- chromium: 104 of 104 passed.
- webkit: 86 of 86 passed.
- live TRX: 61 of 61 passed.

## CI runs on the candidate (all completed)

All runs were created at 23:05:52-53Z. The last one, build-and-test, completed at 23:13:04Z.

| Workflow | Run | Result |
|---|---|---|
| ci.yml (Causa CI) | https://github.com/kemiller2002/indy-init-rehearsal-008/actions/runs/41360391767 | success. The invariant step is continue-on-error (DEV-R8-06) and reports 1 failure out of 35 (INV04). |
| build-and-test.yml | https://github.com/kemiller2002/indy-init-rehearsal-008/actions/runs/41360391741 | success (chromium 104 passed, webkit 86 passed) |
| live.yml | https://github.com/kemiller2002/indy-init-rehearsal-008/actions/runs/41360391689 | success |
| deploy.yml | https://github.com/kemiller2002/indy-init-rehearsal-008/actions/runs/41360391737 | success: build, deploy, **deployed-acceptance** |
| praxis-validation | https://github.com/kemiller2002/indy-init-rehearsal-008/actions/runs/41360391694 | success |
| echelon-foundations | https://github.com/kemiller2002/indy-init-rehearsal-008/actions/runs/41360391957 | success |

## Deployment verification

- **deploy.yml deployed-acceptance** (job 125009945221): success. The job reads the served `deployed-sha.txt` until it equals `GITHUB_SHA`. It then checks index.html for `data-engine="loading"`, `_framework/dotnet.js` and the limen bundle. Its log prints `Deployed c61bfe24751572dadf8110031e78d90a8d31dce0 at https://kevinmmiller.us/indy-init-rehearsal-008` (`raw/deployed-acceptance-job-125009945221.log`). The D-T31-01 fix (grep without `-q`) is in effect.
- **Independent curl check by T34 at 23:11:32Z** (`raw/deployed-check-c61bfe2.log`) found the following:
  - the served `deployed-sha.txt` is `c61bfe24751572dadf8110031e78d90a8d31dce0`;
  - index.html contains `data-engine="loading"`;
  - `_framework/dotnet.js` returned 200;
  - limen `dist/index.js` returned 200.
- **The deployed SHA equals the candidate.** Ledger gate G9 is PASS, with deployment evidence in the manifest. The deployed-live browser suite cannot run because its tests do not exist. Later commits that touch only evidence/ may redeploy the site with a different SHA. The SHA served at the time of this report's check is the one above.

## Live (real-provider) evidence

live.yml run 41360391689, job 125009663660, on the candidate: success, with 61 TRX results, all passed (artifact `live-test-results`, plus the `provider-recordings` artifact).
- It ran against real GitHub, using CAUSA_GITHUB_TOKEN (read) on kemiller2002/causa-data and CAUSA_WRITE_TOKEN (write) on kemiller2002/causa-write-test. No secret was read.
- It covers 60 of 141 SU-DOTNET-LIVE plan identities, including the rewritten end-to-end test `Live.PERSIST007Tests.ReadBackOfFileOver1MB`.
- The other 81 identities are bootstrap stubs.

## Preservation gate (re-run at acceptance, RDP-004)

**Result: exit 2, UNKNOWN. Under RDP-005 that means BLOCKED, so ledger G0 is BLOCKED.** The gate run is recorded in `raw/preservation-gate.result.json`, which holds the exact command, its stderr and the SHA-256 of each input.

```
./praxis requirements preservation verify --baseline <indy-init>/requirements/causa-requirements-baseline-v4.json \
  --source <indy-init at 62df28a559c054226dd4022e9744cf1db06e954f> --derived <indy-init>/requirements-compiled/v4.1.1 \
  --disposition research/requirements-preservation/disposition.json \
  --approvals <indy-init>/kickoff/rehearsal-007/approvals.causa.json --approvals-sha256 63a9ce5b...a8c8 \
  --trusted-approver kemiller2002 --output evidence/T34/raw/preservation-gate.json --json
ERROR file 'research/requirements-preservation/disposition.json' does not exist      (exit 2)
```

- **Cause.** Rehearsal 8 never wrote a `praxis.requirements-disposition/1` file and never derived a Praxis requirements package. T08 (the preflight gate) has no record in `.ros`, `evidence/` or `SCHEDULE-ACTUALS.md`. So there was no first PASS for this run to repeat.
- **Not counted.** The package's own `requirements-compiled/v4.1.1/preservation-report.json` says `PRESERVED`, but it is the compiler's derivation proof and not the Praxis gate. It is recorded but not counted as G0.

## Ledger gates (`evidence/T34/ledger.json`)

| Gate | Status | Basis |
|---|---|---|
| G0 Scope | BLOCKED | preservation gate exit 2 (no disposition) |
| G1 Dependencies | NOT_RUN | no integrity-lock or Forma/Folio smoke gate record in R8. The stack-lock gate (VER-008) step passed in ci.yml and deploy.yml. |
| G2 Interfaces | BLOCKED | SU-OWN-DOTNET has 24 producer/consumer identities absent |
| G3 Domain | FAIL | INV04 RequestBounds (DEV-R8-11) |
| G4 Frozen data | NOT_RUN | no G4 gate record (T15/T15B replay 5/5 golden, 40/40 negatives) |
| G5 UI | NOT_RUN | 7 of 44 P0 UI/WASM/WDL requirements PASS, 37 NOT_RUN |
| G6 Persistence | NOT_RUN | 19 of 46 PERSIST requirements PASS, 26 NOT_RUN, 1 BLOCKED |
| G7 Defect class | FAIL | open HIGH classes: unwritten bound tests; INV04 awaiting owner. T33-F-01 (fix c16ea27) and T33-F-08 (fix 09c7c27) have passing tests at the candidate but no independent review of the fix, so both are BLOCKED. |
| G8 Candidate | FAIL | SU-BROWSER-LIVE, SU-VERIFIER and SU-PROCESS-RECORDS have no results; SU-DOTNET-INVARIANTS has a failure |
| G9 Deployment | PASS | deployed SHA = candidate; deployed-acceptance green on the same SHA |

Open owner decisions (`unresolved_decisions`): DEV-R8-11, the owner approval for AR-008-O2/AR-024-O1/O2, DEV-R8-04, DEV-R8-05, and DEV-R8-09/10.

## Measurements (timed-execution-manifest; unavailable values recorded as unavailable)

| Measurement | Value |
|---|---|
| first green CI | T+273 s (T10, 17:35:57Z) |
| first candidate | T+15698 s (T31, b0ba767, 21:53:02Z) |
| final candidate | c61bfe2 committed 23:05:02Z (T+20618 s) |
| accepted verdict | unavailable (not accepted) |
| candidates | 2 (b0ba767, c61bfe2) |
| CI runs in repository at check | 548 (created; per-run executed/failed totals and queue time not summed: unavailable) |
| candidate CI wall time | ci 81 s, chromium 412 s, webkit 362 s, live 197 s, deploy 2 min 33 s to deployed-acceptance |
| tokens per agent | unavailable in this report (recorded per work item in Praxis telemetry ingests in `.ros`; not summed here) |
| defect-class recurrence against r7-lessons | unavailable (not analysed) |
| verified requirements | 50 of 161 P0 |

## T34 covers

The obligations of PERSIST-001, PERSIST-026 and PERSIST-055, which T34 covers, are **not verified**: their deterministic acceptance tests are stubs, and PERSIST-026-O2 needs the absent SU-VERIFIER job. The R14/R16 and doc covers are met only as far as this ledger, gate run and verdict go.

## Unverified obligations by cause (357 = 1 failed + 356 not run)

An obligation appears under every cause that applies to it, so the groups overlap.

- **SU-BROWSER-CHROMIUM: spec file absent** (152 obligations): PERSIST-002-O1, PERSIST-002-O2, PERSIST-002-O3, PERSIST-002-O4, PERSIST-002-O5, PERSIST-003-O1, PERSIST-003-O2, PERSIST-003-O3, PERSIST-003-O4, PERSIST-003-O5, PERSIST-004-O1, PERSIST-004-O2, PERSIST-004-O3, PERSIST-004-O4, PERSIST-017-O1, PERSIST-017-O2, PERSIST-017-O3, PERSIST-018-O1, PERSIST-018-O2, PERSIST-018-O3, PERSIST-018-O4, PERSIST-019-O1, PERSIST-019-O2, PERSIST-019-O3, PERSIST-019-O4, PERSIST-020-O1, PERSIST-020-O2, PERSIST-020-O3, PERSIST-020-O4, PERSIST-020-O5, PERSIST-020-O6, PERSIST-022-O1, PERSIST-022-O2, PERSIST-022-O3, PERSIST-022-O4, PERSIST-024-O1, PERSIST-024-O2, PERSIST-024-O3, PERSIST-024-O4, PERSIST-024-O5, PERSIST-031-O1, PERSIST-031-O10, PERSIST-031-O11, PERSIST-031-O12, PERSIST-031-O13, PERSIST-031-O14, PERSIST-031-O15, PERSIST-031-O16, PERSIST-031-O17, PERSIST-031-O18, PERSIST-031-O19, PERSIST-031-O2, PERSIST-031-O20, PERSIST-031-O21, PERSIST-031-O22, PERSIST-031-O23, PERSIST-031-O24, PERSIST-031-O3, PERSIST-031-O4, PERSIST-031-O5, PERSIST-031-O6, PERSIST-031-O7, PERSIST-031-O8, PERSIST-031-O9, PERSIST-040-O1, PERSIST-040-O2, PERSIST-040-O3, PERSIST-040-O4, PERSIST-040-O5, PERSIST-040-O6, PERSIST-044-O1, PERSIST-044-O2, PERSIST-044-O3, PERSIST-044-O4, PERSIST-044-O5, PERSIST-044-O6, PERSIST-046-O1, PERSIST-046-O2, PERSIST-046-O3, PERSIST-046-O4, PERSIST-046-O5, PERSIST-046-O6, PERSIST-048-O1, PERSIST-048-O2, PERSIST-048-O3, PERSIST-048-O4, PERSIST-048-O5, PERSIST-050-O1, PERSIST-050-O2, PERSIST-050-O3, PERSIST-050-O4, PERSIST-054-O1, PERSIST-054-O2, PERSIST-054-O3, PERSIST-054-O4, UI-003-O1, UI-003-O2, UI-003-O3, UI-003-O4, UI-005-O1, UI-005-O2, UI-005-O3, UI-005-O4, UI-015-O1, UI-015-O2, UI-015-O3, UI-015-O4, UI-026-O1, UI-026-O2, UI-026-O3, UI-026-O4, UI-030-O1, UI-030-O2, UI-030-O3, UI-030-O4, UI-032-O1, UI-032-O2, UI-033-O1, UI-033-O2, UI-034-O1, UI-034-O2, UI-034-O3, UI-038-O1, UI-038-O2, UI-039-O1, UI-039-O2, UI-040-O1, UI-040-O2, WDL-003-O1, WDL-003-O2, WDL-004-O1, WDL-004-O2, WDL-005-O1, WDL-005-O2, WDL-006-O1, WDL-006-O2, WDL-007-O1, WDL-007-O2, WDL-008-O1, WDL-008-O2, WDL-009-O1, WDL-009-O2, WDL-010-O1, WDL-010-O2, WDL-011-O1, WDL-011-O2, WDL-012-O1, WDL-012-O2, WDL-014-O1, WDL-014-O2, WDL-015-O1, WDL-015-O2
- **SU-DOTNET-LIVE: bound .NET test absent (bootstrap stub or missing identity)** (81 obligations): EXT-DATA-001-O1, EXT-DATA-001-O2, EXT-DATA-002-O1, EXT-DATA-002-O2, EXT-DATA-003-O1, EXT-DATA-003-O2, EXT-DATA-004-O1, EXT-DATA-004-O2, EXT-DATA-004-O3, EXT-DATA-004-O4, EXT-DATA-005-O1, EXT-DATA-005-O2, EXT-DATA-006-O1, EXT-DATA-006-O2, EXT-DATA-007-O1, EXT-DATA-007-O2, EXT-DATA-008-O1, EXT-DATA-008-O2, EXT-DATA-009-O1, EXT-DATA-009-O2, EXT-DATA-010-O1, EXT-DATA-010-O2, EXT-DATA-010-O3, EXT-DATA-012-O1, EXT-DATA-012-O2, EXT-DATA-012-O3, PF-001-O1, PF-001-O2, PF-001-O3, PF-002-O1, PF-002-O2, PF-003-O1, PF-003-O2, PF-003-O3, PF-003-O4, PF-004-O1, PF-004-O2, PF-004-O3, PF-005-O1, PF-005-O2, PF-006-O1, PF-006-O2, PF-007-O1, PF-007-O2, PF-008-O1, PF-008-O2, PF-009-O1, PF-009-O2, PF-009-O3, PF-011-O1, PF-011-O2, PF-011-O3, PF-011-O4, PF-011-O5, PF-011-O6, PF-012-O1, PF-012-O2, PF-012-O3, PF-013-O1, PF-013-O2, PF-013-O3, PF-013-O4, PF-014-O1, PF-014-O2, PF-014-O3, PF-014-O4, PF-015-O1, PF-015-O2, PF-015-O3, PF-015-O4, PF-015-O5, PF-016-O1, PF-016-O2, PF-017-O1, PF-017-O2, PF-018-O1, PF-018-O2, PF-019-O1, PF-019-O2, PF-020-O1, PF-020-O2
- **SU-DOTNET-DETERMINISTIC: bound .NET test absent (bootstrap stub or missing identity)** (49 obligations): AR-014-O1, AR-014-O2, AR-030-O1, AR-030-O2, AR-035-O2, AR-035-O3, PERSIST-001-O1, PERSIST-001-O2, PERSIST-001-O3, PERSIST-001-O4, PERSIST-001-O5, PERSIST-012-O3, PERSIST-026-O1, PERSIST-026-O2, PERSIST-026-O3, PERSIST-026-O4, PERSIST-026-O5, PERSIST-052-O1, PERSIST-052-O2, PERSIST-052-O3, PERSIST-052-O4, PERSIST-052-O5, PERSIST-053-O1, PERSIST-053-O2, PERSIST-053-O3, PERSIST-053-O4, PERSIST-055-O1, PERSIST-055-O2, PERSIST-055-O3, PERSIST-055-O4, VER-001-O1, VER-001-O2, VER-001-O3, VER-002-O1, VER-002-O2, VER-002-O3, VER-003-O1, VER-003-O2, VER-003-O3, VER-004-O1, VER-004-O2, VER-004-O3, VER-005-O1, VER-005-O2, VER-006-O1, VER-006-O2, VER-006-O3, VER-010-O1, VER-010-O2
- **SU-BROWSER-CHROMIUM: spec exists, bound title did not run** (44 obligations): PERSIST-030-O2, PERSIST-030-O5, PERSIST-030-O8, PERSIST-033-O3, UI-001-O2, UI-001-O3, UI-001-O4, UI-001-O5, UI-004-O1, UI-004-O3, UI-004-O4, UI-010-O3, UI-010-O4, UI-011-O5, UI-013-O2, UI-013-O3, UI-016-O10, UI-016-O14, UI-016-O15, UI-016-O16, UI-016-O17, UI-016-O18, UI-016-O2, UI-016-O3, UI-016-O4, UI-016-O5, UI-016-O6, UI-016-O7, UI-017-O4, UI-017-O5, UI-021-O3, UI-022-O3, UI-022-O4, UI-022-O5, UI-022-O6, UI-023-O2, UI-023-O3, UI-023-O4, UI-024-O1, UI-024-O2, UI-024-O6, UI-025-O3, UI-031-O1, UI-031-O3
- **SU-BROWSER-WEBKIT: spec file absent** (33 obligations): PERSIST-020-O1, PERSIST-020-O2, PERSIST-020-O3, PERSIST-020-O4, PERSIST-020-O5, PERSIST-020-O6, PERSIST-040-O1, PERSIST-040-O2, PERSIST-040-O3, PERSIST-040-O4, PERSIST-040-O5, PERSIST-040-O6, PERSIST-046-O1, PERSIST-046-O2, PERSIST-046-O3, PERSIST-046-O4, PERSIST-046-O5, PERSIST-046-O6, PERSIST-048-O1, PERSIST-048-O2, PERSIST-048-O3, PERSIST-048-O4, PERSIST-048-O5, PERSIST-054-O1, PERSIST-054-O2, PERSIST-054-O3, PERSIST-054-O4, UI-011-O1, UI-034-O1, UI-034-O2, UI-034-O3, WDL-007-O1, WDL-007-O2
- **SU-PROCESS-RECORDS: no Praxis process evidence of the bound kind** (24 obligations): AR-005-O1, AR-005-O2, AR-012-O1, AR-012-O2, AR-015-O1, AR-017-O1, AR-017-O2, AR-021-O1, AR-021-O2, PF-019-O1, UI-021-O3, UI-030-O3, UI-040-O2, VER-001-O1, VER-001-O2, VER-001-O3, VER-002-O1, VER-002-O2, VER-002-O3, VER-003-O1, VER-003-O2, VER-004-O3, VER-010-O1, VER-010-O2
- **SU-BROWSER-LIVE: tests/browser/live does not exist** (17 obligations): PERSIST-041-O1, PERSIST-041-O2, PERSIST-041-O3, PERSIST-041-O4, PERSIST-041-O5, PERSIST-049-O1, PERSIST-049-O2, PERSIST-049-O3, PERSIST-049-O4, PERSIST-049-O5, UI-006-O1, UI-006-O2, UI-006-O3, UI-006-O4, UI-006-O5, UI-006-O6, UI-006-O7
- **SU-BROWSER-WEBKIT: spec exists, bound title did not run** (16 obligations): PERSIST-030-O2, PERSIST-030-O5, PERSIST-030-O8, UI-011-O5, UI-016-O10, UI-016-O14, UI-016-O15, UI-016-O16, UI-016-O17, UI-016-O18, UI-016-O2, UI-016-O3, UI-016-O4, UI-016-O5, UI-016-O6, UI-016-O7
- **owner decision: no owner-approval record** (3 obligations): AR-008-O2, AR-024-O1, AR-024-O2
- **SU-VERIFIER: no independent verifier job** (2 obligations): PERSIST-026-O2, PERSIST-052-O3
- **failed bound test: `Causa.Invariants.Property.INV04.RequestBounds` (DEV-R8-11, owner decision)** (1 obligation): PERSIST-016-O5

## Reproduce

Run these commands from the repository root:

```
python3 evidence/T34/raw/build-matrix.py evidence/T34/T34-matrix.json   # inputs: raw/*-results.json parsed by raw/trx.py and raw/pwlog.py
python3 evidence/T34/raw/build-ledger.py
python3 <indy-init>/tools/validate-acceptance-ledger.py evidence/T34/ledger.json
REPORT=evidence/T34/T34-ACCEPTANCE-REPORT.md bash evidence/T34/raw/run-verdict.sh
```
