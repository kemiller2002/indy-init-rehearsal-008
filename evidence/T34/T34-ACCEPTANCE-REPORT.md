# T34 final acceptance: preservation gate, ledger, computed verdict, measurements

- Work item: WI-R8-T34 (integration lane), regenerated for the final candidate. Generated 2026-11-01T23:49Z.
- **Candidate: `5e2717665b696a0c70175c7cacfc5702c5b2058d`**. coordinator message: new final candidate 5e27176 (main after tests-only PRs #54 FINALDET and #55 FINALUI on c61bfe2; src/ and web/ byte-identical to c61bfe2).
- **Previous candidate: `c61bfe2`** (PR #53): 256 verified, 1 failed, 356 not run of 613. Computed verdict INVALID. Its raw inputs are kept in `evidence/T34/raw/c61bfe2/` and `raw/previous-candidate-c61bfe2.json`.
- Plan: v4 `VP-1e3c66bcc958`, which has 613 obligations, all P0 and none P1. It runs through `requirements-compiled/v4.1.1/verification-execution-plan.json`: 11 suites, 660 plan tests plus 140 of our own.
- Method: the same as T31's, regenerated at the candidate with the same scripts and rules. Results come only from CI runs on the candidate: the TRX artifacts for .NET and the Playwright list reporter in the job logs for the browser suites. An obligation counts as verified only when every test identity bound to it passed at the candidate SHA. Nothing was estimated, and no suite was run locally.
- Files:
  - `T34-matrix.json`: one row per obligation.
  - `ledger.json`: `causa.acceptance-ledger/2`.
  - `evidence-manifest.json` plus `acceptance-evidence/`: `causa.acceptance-evidence-manifest/1`.
  - `raw/`: scripts, parsed CI results, gate run, deployment checks and verdict output.
- Parser fix in this regeneration: `raw/pwlog.py` now accepts Playwright durations in minutes, such as `(1.3m)`. Before the fix, one WebKit title that ran for 1.3 minutes was dropped. No c61bfe2 log line has such a duration, so the previous numbers are unaffected.

## Computed verdict

<!-- acceptance-verdict: INVALID -->

`tools/compute-acceptance-verdict.py` computed **INVALID** (exit 3). This is the only verdict in this report. **The candidate cannot be released.**

The verdict tool reports INVALID because one of its trust inputs fails before it compares the ledger: the pinned approvals file. That file is `kickoff/rehearsal-007/approvals.causa.json`, the latest one, and it holds APR-V411-OWNER-1/2. It fails in two ways:
- No approval names an immutable authority source (`source.repository/commit/path`).
- The changes do not use the `requirement_id` and `action` identity that the verifier expects.

Only the owner can fix this; no agent writes approvals.

Even with valid trust inputs, the ledger would compute FAILED:
- Gates: G0 BLOCKED, G1 NOT_RUN, G2 BLOCKED, G3 FAIL, G4 NOT_RUN, G5 NOT_RUN, G6 NOT_RUN, G7 FAIL, G8 FAIL, G9 PASS.
- P0 requirements: 3 BLOCKED, 107 NOT_RUN, 51 PASS.

Exact command: `evidence/T34/raw/run-verdict.sh`. Output: `raw/verdict.json`. Input digests: `raw/verdict-inputs.txt`. The inputs are:
- Baseline: `requirements/causa-requirements-baseline-v4.json`, sha256 `f86a3539e48ee8dda7d99d5f4f5546a18e2397a82d308a277c8f346edb2a8f1a`, source `62df28a559c054226dd4022e9744cf1db06e954f`.
- Approvals: sha256 `63a9ce5b8a5cd160fff7d485bbd877276625b2093e894e3b7fe75b57b9d7a8c8`.
- Trusted operator: `kemiller2002`.
- Evidence manifest: sha256 `b33869f8f044317138b85c37608672834e3d7b97e69c0b146c528efed1e0db82`.
- Trusted source SHA: `5e2717665b696a0c70175c7cacfc5702c5b2058d`.

## Counts (P0; the frozen plan has no P1 obligations)

| Status | Obligations | Previous (c61bfe2) |
|---|---|---|
| verified (every bound test passed at the candidate) | **266** | 256 |
| failed (a bound test failed) | **1**: PERSIST-016-O5 | 1 |
| not run | **346**: 343 with a bound test that produced no result, plus 3 where only the owner-approval record is missing (AR-008-O2, AR-024-O1, AR-024-O2) | 356 |
| **total** | **613** | 613 |

Requirements with every obligation verified: **51 of 161** P0 (c61bfe2: 50). They are: AR-001, AR-002, AR-003, AR-004, AR-006, AR-007, AR-009, AR-010, AR-011, AR-013, AR-018, AR-019, AR-020, AR-022, AR-023, AR-027, AR-028, AR-029, AR-031, AR-034, EXT-DATA-011, EXT-DATA-013, PERSIST-005, PERSIST-006, PERSIST-007, PERSIST-008, PERSIST-009, PERSIST-010, PERSIST-011, PERSIST-013, PERSIST-014, PERSIST-015, PERSIST-021, PERSIST-023, PERSIST-025, PERSIST-032, PERSIST-042, PERSIST-043, PERSIST-045, PERSIST-047, PERSIST-051, UI-014, UI-018, UI-019, UI-020, UI-023, UI-029, VER-007, VER-008, WDL-001, WDL-002.

Test identities: {'pass': 379, 'not-run': 420, 'fail': 1}.

### Live suite: attempt 1 and the single permitted re-run

live.yml run 41362398338 **attempt 1** (job 125015370042, 23:30-23:32Z) **failed**: 58 of 61 passed. PERSIST-009-O2, PERSIST-013-O3 and PERSIST-013-O4 failed because the token had used up GitHub's primary rate limit. The first failing test logged `repository unreadable: Error (RateLimited (Primary (Some 1793576603L)))`, a limit that resets at 23:43:23Z. The other two logged `branch head unreadable`, which is the same 403. These tests are unchanged since c61bfe2, where they passed, and #54/#55 do not touch Live. Attempt 1 results: `raw/ci-live-results-attempt1.json`. The coordinator re-ran the failed job once, after the reset; the rules allow one re-run. **Attempt 2** (job 125018816940, 23:44-23:47Z) **succeeded with 61 of 61**. SU-DOTNET-LIVE uses attempt 2 (`raw/ci-live-results.json`).

### Rules applied (T33 review)

- **PERSIST-007-O1:** verified. T32L landed, and every bound identity passed at the candidate: the deterministic and live `PERSIST007Tests.PERSIST-007-O1`, and the rewritten end-to-end live `ReadBackOfFileOver1MB`. The live one passed in both attempts.
- **WebKit:** any failure in the WebKit job would count as failed. The job had no failures and no retries. INV-06 (`every store empty of investigation content [webkit]`) and INV-21 (`no credential in storage, URL or artifact [webkit]`) passed at the candidate.
- **Owner decisions:** INV04 is DEV-R8-11, an owner decision. AR-008-O2 and AR-024-O1/O2 have no owner-approval record. The T33 review record counts for `praxis:independent-review`; it ratifies no owner decision.

## Per-suite results

| Suite | Planned | Pass | Fail | Not run | Previous pass/fail (c61bfe2) | Evidence |
|---|---|---|---|---|---|---|
| SU-OWN-DOTNET | 137 | 113 | 0 | 24 | 113/0 | https://github.com/kemiller2002/indy-init-rehearsal-008/actions/runs/41362398291 (TRX artifact causa-ci-results) |
| SU-OWN-BROWSER | 3 | 3 | 0 | 0 | 3/0 | https://github.com/kemiller2002/indy-init-rehearsal-008/actions/runs/41362398320/job/125015444533 (Playwright list reporter in the job log) |
| SU-DOTNET-DETERMINISTIC | 116 | 71 | 0 | 45 | 67/0 | https://github.com/kemiller2002/indy-init-rehearsal-008/actions/runs/41362398291 (TRX artifact causa-ci-results) |
| SU-DOTNET-INVARIANTS | 21 | 20 | 1 | 0 | 20/1 | https://github.com/kemiller2002/indy-init-rehearsal-008/actions/runs/41362398291 step Invariant suite (invariants.trx) |
| SU-BROWSER-CHROMIUM | 281 | 88 | 0 | 193 | 82/0 | https://github.com/kemiller2002/indy-init-rehearsal-008/actions/runs/41362398320/job/125015444533 (Playwright list reporter in the job log) |
| SU-BROWSER-WEBKIT | 72 | 23 | 0 | 49 | 23/0 | https://github.com/kemiller2002/indy-init-rehearsal-008/actions/runs/41362398320/job/125015444236 (Playwright list reporter in the job log) |
| SU-DOTNET-LIVE | 141 | 60 | 0 | 81 | 60/0 | https://github.com/kemiller2002/indy-init-rehearsal-008/actions/runs/41362398338/job/125018816940 (attempt 2; TRX artifact live-test-results id 13105194788) |
| SU-BROWSER-LIVE | 17 | 0 | 0 | 17 | 0/0 | none |
| SU-VERIFIER | 2 | 0 | 0 | 2 | 0/0 | none |
| SU-REVIEW-RECORDS | 2 | 1 | 0 | 1 | 1/0 | evidence/reviews/T33-final-review.md (independent-review only) |
| SU-PROCESS-RECORDS | 8 | 0 | 0 | 8 | 0/0 | none |

Not-run causes: bootstrap stub files (deterministic and live .NET), spec files that are absent or titles that did not run (chromium and webkit), `tests/browser/live` being absent, no SU-VERIFIER job, no Praxis process records, and 24 SU-OWN-DOTNET producer/consumer identities absent from source.

## CI runs on the candidate

| Workflow | Run | Result |
|---|---|---|
| ci | https://github.com/kemiller2002/indy-init-rehearsal-008/actions/runs/41362398291 | success (non-live 268/268; invariant step continue-on-error per DEV-R8-06: 34/35, INV04 failed) |
| build-and-test | https://github.com/kemiller2002/indy-init-rehearsal-008/actions/runs/41362398320 | success (chromium 110 passed in 5.4m; webkit 92 passed in 8.0m; no failures, no retries) |
| live | https://github.com/kemiller2002/indy-init-rehearsal-008/actions/runs/41362398338 | success on attempt 2 (61/61 passed; used for SU-DOTNET-LIVE). Attempt 1 failed: 58/61, rate limit (see report). |
| deploy | https://github.com/kemiller2002/indy-init-rehearsal-008/actions/runs/41362398296 | success (build, deploy, deployed-acceptance) |
| praxis-validation | https://github.com/kemiller2002/indy-init-rehearsal-008/actions/runs/41362398287 | success |
| echelon-foundations | https://github.com/kemiller2002/indy-init-rehearsal-008/actions/runs/41362398791 | success |

## Deployment verification

- **deploy.yml deployed-acceptance** (https://github.com/kemiller2002/indy-init-rehearsal-008/actions/runs/41362398296/job/125015912543): success. The job reads the served `deployed-sha.txt` until it equals `GITHUB_SHA`. It then checks `data-engine="loading"`, `_framework/dotnet.js` and the limen bundle. Its log prints `Deployed 5e2717665b696a0c70175c7cacfc5702c5b2058d at https://kevinmmiller.us/indy-init-rehearsal-008` (`evidence/T34/raw/deployed-acceptance-job-125015912543.log`).
- **Independent curl check by T34** (`raw/deployed-check-5e27176.log`): independent check by WI-R8-T34 at 2026-11-01T23:41:12Z; deployed-sha.txt: 5e2717665b696a0c70175c7cacfc5702c5b2058d; index.html data-engine=loading: 1; _framework/dotnet.js: 200; limen dist/index.js: 200.
- **The deployed SHA equals the candidate.** Ledger gate G9 is PASS, with deployment evidence in the manifest. The deployed-live browser suite cannot run because its tests do not exist.

## Live (real-provider) evidence

live.yml run 41362398338 on the candidate, attempt 2 (job 125018816940): success, 61 of 61 TRX results passed (artifact `live-test-results`, plus `provider-recordings`). It ran against real GitHub: CAUSA_GITHUB_TOKEN read kemiller2002/causa-data and CAUSA_WRITE_TOKEN wrote to kemiller2002/causa-write-test. No secret value was read. It covers 60 of 141 SU-DOTNET-LIVE plan identities; the other 81 are bootstrap stubs. Attempt 1 failed only on the rate limit described above.

## Preservation gate (re-run at acceptance, RDP-004)

**Result: exit 2 (UNKNOWN). Under RDP-005 that is BLOCKED, so ledger G0 is BLOCKED.** The candidate change (tests only) does not alter the gate inputs, so the run in `raw/preservation-gate.result.json` still applies. That file records the exact command, its stderr and the input SHA-256s. The run fails with `ERROR file 'research/requirements-preservation/disposition.json' does not exist`.
- Rehearsal 8 never wrote a `praxis.requirements-disposition/1` file or a derived Praxis requirements package.
- T08 has no record in `.ros`, `evidence/` or `SCHEDULE-ACTUALS.md`.
- The package's `requirements-compiled/v4.1.1/preservation-report.json` reports `PRESERVED`. That report is the compiler's derivation proof, not the Praxis gate, so it is not counted as G0.

## Ledger gates

| Gate | Status | Basis |
|---|---|---|
| G0 Scope | BLOCKED | preservation gate re-run exit 2: ERROR file 'research/requirements-preservation/disposition.json' does not exist (RDP-005: exit 2 is UNKNOWN, acceptance BLOCKED). Evidence: evidence/T34/raw/preservation-gate.result.json |
| G1 Dependencies | NOT_RUN | no G1 gate evidence record was produced in Rehearsal 8; CI builds with the locked set, but no integrity-lock or Forma/Folio smoke record exists |
| G2 Interfaces | BLOCKED | SU-OWN-DOTNET: 113 pass, 0 fail, 24 not run (producer/consumer identities absent from source) |
| G3 Domain | FAIL | SU-DOTNET-INVARIANTS: 20 pass, 1 fail, 0 not run (INV04 RequestBounds, DEV-R8-11) |
| G4 Frozen data | NOT_RUN | no G4 gate evidence record: T15/T15B replay 5/5 golden and 40/40 negatives in the contract tests; no gate record against the 90-fixture set |
| G5 UI | NOT_RUN | 44 P0 requirements: NOT_RUN 36, PASS 8 |
| G6 Persistence | NOT_RUN | 46 P0 requirements: BLOCKED 1, NOT_RUN 26, PASS 19 |
| G7 Defect class | FAIL | open HIGH defect classes (see defect_classes): unwritten bound tests; INV04 (DEV-R8-11) awaits an owner decision |
| G8 Candidate | FAIL | required suites with no result: SU-BROWSER-LIVE, SU-VERIFIER, SU-PROCESS-RECORDS; suites with failures: SU-DOTNET-INVARIANTS |
| G9 Deployment | PASS | deploy.yml deployed-acceptance succeeded on the candidate and the independent check read the candidate SHA |

Open owner decisions (`unresolved_decisions`): DEV-R8-11 (INV04 RequestBounds fails; PERSIST-016-O5); AR-008-O2/AR-024-O1/AR-024-O2 (need an owner-approval record; none exists); DEV-R8-04 (central FSharp.Core entry); DEV-R8-05 (forma-workflow 1.2.0 not applied); DEV-R8-09/DEV-R8-10 (frozen-contract gaps absorbed in implementation).

## Measurements (timed-execution-manifest; unavailable values recorded as unavailable)

| Measurement | Value |
|---|---|
| first green CI | T+273 s (T10 first green CI 17:35:57Z vs T0 17:31:24Z (evidence/SCHEDULE-ACTUALS.md)) |
| first candidate | T+15698 s (T31 candidate b0ba767 committed 21:53:02Z) |
| final candidate | 5e27176, merged 23:30Z (about T+21524 s) |
| accepted verdict | unavailable (not accepted) |
| candidates | 3 (b0ba767 (T31), c61bfe2 (T34 first), 5e27176 (T34 final)) |
| CI | candidate 5e27176 runs created 23:30:08Z; ci done 23:31:41, deploy 23:32:24, live attempt 1 23:32:49 (failed, rate limit), build-and-test 23:39:11. Totals not summed (unavailable). Live attempt 2 (re-run by the coordinator) 23:44:08-23:47:31Z: success. |
| tokens per agent | unavailable in this report (unavailable here: per-agent tokens are in the Praxis telemetry ingests (.ros), not summed by T34) |
| defect-class recurrence | unavailable (unavailable: no recurrence analysis against analysis/r7-lessons.json was run) |
| verified requirements | 51 of 161 P0 |

## T34 covers

PERSIST-001, PERSIST-026 and PERSIST-055: PERSIST-001 Counter({'not-run': 5}); PERSIST-026 Counter({'not-run': 5}); PERSIST-055 Counter({'not-run': 4}). Their deterministic acceptance tests are stubs, and PERSIST-026-O2 also needs the SU-VERIFIER job, which does not exist. For the R14/R16 and doc covers, T34 delivers this ledger, the gate run and the computed verdict.

## Unverified obligations by cause (347 = 1 failed + 346 not run)

An obligation is listed under every cause that applies to it.

- **SU-BROWSER-CHROMIUM: spec file absent** (152): PERSIST-002-O1, PERSIST-002-O2, PERSIST-002-O3, PERSIST-002-O4, PERSIST-002-O5, PERSIST-003-O1, PERSIST-003-O2, PERSIST-003-O3, PERSIST-003-O4, PERSIST-003-O5, PERSIST-004-O1, PERSIST-004-O2, PERSIST-004-O3, PERSIST-004-O4, PERSIST-017-O1, PERSIST-017-O2, PERSIST-017-O3, PERSIST-018-O1, PERSIST-018-O2, PERSIST-018-O3, PERSIST-018-O4, PERSIST-019-O1, PERSIST-019-O2, PERSIST-019-O3, PERSIST-019-O4, PERSIST-020-O1, PERSIST-020-O2, PERSIST-020-O3, PERSIST-020-O4, PERSIST-020-O5, PERSIST-020-O6, PERSIST-022-O1, PERSIST-022-O2, PERSIST-022-O3, PERSIST-022-O4, PERSIST-024-O1, PERSIST-024-O2, PERSIST-024-O3, PERSIST-024-O4, PERSIST-024-O5, PERSIST-031-O1, PERSIST-031-O10, PERSIST-031-O11, PERSIST-031-O12, PERSIST-031-O13, PERSIST-031-O14, PERSIST-031-O15, PERSIST-031-O16, PERSIST-031-O17, PERSIST-031-O18, PERSIST-031-O19, PERSIST-031-O2, PERSIST-031-O20, PERSIST-031-O21, PERSIST-031-O22, PERSIST-031-O23, PERSIST-031-O24, PERSIST-031-O3, PERSIST-031-O4, PERSIST-031-O5, PERSIST-031-O6, PERSIST-031-O7, PERSIST-031-O8, PERSIST-031-O9, PERSIST-040-O1, PERSIST-040-O2, PERSIST-040-O3, PERSIST-040-O4, PERSIST-040-O5, PERSIST-040-O6, PERSIST-044-O1, PERSIST-044-O2, PERSIST-044-O3, PERSIST-044-O4, PERSIST-044-O5, PERSIST-044-O6, PERSIST-046-O1, PERSIST-046-O2, PERSIST-046-O3, PERSIST-046-O4, PERSIST-046-O5, PERSIST-046-O6, PERSIST-048-O1, PERSIST-048-O2, PERSIST-048-O3, PERSIST-048-O4, PERSIST-048-O5, PERSIST-050-O1, PERSIST-050-O2, PERSIST-050-O3, PERSIST-050-O4, PERSIST-054-O1, PERSIST-054-O2, PERSIST-054-O3, PERSIST-054-O4, UI-003-O1, UI-003-O2, UI-003-O3, UI-003-O4, UI-005-O1, UI-005-O2, UI-005-O3, UI-005-O4, UI-015-O1, UI-015-O2, UI-015-O3, UI-015-O4, UI-026-O1, UI-026-O2, UI-026-O3, UI-026-O4, UI-030-O1, UI-030-O2, UI-030-O3, UI-030-O4, UI-032-O1, UI-032-O2, UI-033-O1, UI-033-O2, UI-034-O1, UI-034-O2, UI-034-O3, UI-038-O1, UI-038-O2, UI-039-O1, UI-039-O2, UI-040-O1, UI-040-O2, WDL-003-O1, WDL-003-O2, WDL-004-O1, WDL-004-O2, WDL-005-O1, WDL-005-O2, WDL-006-O1, WDL-006-O2, WDL-007-O1, WDL-007-O2, WDL-008-O1, WDL-008-O2, WDL-009-O1, WDL-009-O2, WDL-010-O1, WDL-010-O2, WDL-011-O1, WDL-011-O2, WDL-012-O1, WDL-012-O2, WDL-014-O1, WDL-014-O2, WDL-015-O1, WDL-015-O2
- **SU-DOTNET-LIVE: bound .NET test absent (bootstrap stub or missing identity)** (81): EXT-DATA-001-O1, EXT-DATA-001-O2, EXT-DATA-002-O1, EXT-DATA-002-O2, EXT-DATA-003-O1, EXT-DATA-003-O2, EXT-DATA-004-O1, EXT-DATA-004-O2, EXT-DATA-004-O3, EXT-DATA-004-O4, EXT-DATA-005-O1, EXT-DATA-005-O2, EXT-DATA-006-O1, EXT-DATA-006-O2, EXT-DATA-007-O1, EXT-DATA-007-O2, EXT-DATA-008-O1, EXT-DATA-008-O2, EXT-DATA-009-O1, EXT-DATA-009-O2, EXT-DATA-010-O1, EXT-DATA-010-O2, EXT-DATA-010-O3, EXT-DATA-012-O1, EXT-DATA-012-O2, EXT-DATA-012-O3, PF-001-O1, PF-001-O2, PF-001-O3, PF-002-O1, PF-002-O2, PF-003-O1, PF-003-O2, PF-003-O3, PF-003-O4, PF-004-O1, PF-004-O2, PF-004-O3, PF-005-O1, PF-005-O2, PF-006-O1, PF-006-O2, PF-007-O1, PF-007-O2, PF-008-O1, PF-008-O2, PF-009-O1, PF-009-O2, PF-009-O3, PF-011-O1, PF-011-O2, PF-011-O3, PF-011-O4, PF-011-O5, PF-011-O6, PF-012-O1, PF-012-O2, PF-012-O3, PF-013-O1, PF-013-O2, PF-013-O3, PF-013-O4, PF-014-O1, PF-014-O2, PF-014-O3, PF-014-O4, PF-015-O1, PF-015-O2, PF-015-O3, PF-015-O4, PF-015-O5, PF-016-O1, PF-016-O2, PF-017-O1, PF-017-O2, PF-018-O1, PF-018-O2, PF-019-O1, PF-019-O2, PF-020-O1, PF-020-O2
- **SU-DOTNET-DETERMINISTIC: bound .NET test absent (bootstrap stub or missing identity)** (45): AR-014-O1, AR-014-O2, AR-030-O1, AR-030-O2, AR-035-O2, AR-035-O3, PERSIST-001-O1, PERSIST-001-O2, PERSIST-001-O3, PERSIST-001-O4, PERSIST-001-O5, PERSIST-012-O3, PERSIST-026-O1, PERSIST-026-O2, PERSIST-026-O3, PERSIST-026-O4, PERSIST-026-O5, PERSIST-052-O1, PERSIST-052-O2, PERSIST-052-O3, PERSIST-052-O4, PERSIST-052-O5, PERSIST-053-O1, PERSIST-053-O2, PERSIST-053-O3, PERSIST-053-O4, PERSIST-055-O1, PERSIST-055-O2, PERSIST-055-O3, PERSIST-055-O4, VER-001-O1, VER-001-O2, VER-001-O3, VER-002-O1, VER-002-O2, VER-002-O3, VER-003-O1, VER-003-O2, VER-004-O3, VER-005-O2, VER-006-O1, VER-006-O2, VER-006-O3, VER-010-O1, VER-010-O2
- **SU-BROWSER-CHROMIUM: spec exists, bound title did not run** (38): PERSIST-030-O2, PERSIST-030-O5, PERSIST-030-O8, PERSIST-033-O3, UI-001-O2, UI-001-O3, UI-001-O4, UI-001-O5, UI-004-O1, UI-004-O3, UI-010-O4, UI-011-O5, UI-013-O3, UI-016-O10, UI-016-O14, UI-016-O15, UI-016-O16, UI-016-O17, UI-016-O18, UI-016-O2, UI-016-O3, UI-016-O4, UI-016-O5, UI-016-O6, UI-016-O7, UI-017-O4, UI-017-O5, UI-021-O3, UI-022-O3, UI-022-O4, UI-022-O5, UI-022-O6, UI-024-O1, UI-024-O2, UI-024-O6, UI-025-O3, UI-031-O1, UI-031-O3
- **SU-BROWSER-WEBKIT: spec file absent** (33): PERSIST-020-O1, PERSIST-020-O2, PERSIST-020-O3, PERSIST-020-O4, PERSIST-020-O5, PERSIST-020-O6, PERSIST-040-O1, PERSIST-040-O2, PERSIST-040-O3, PERSIST-040-O4, PERSIST-040-O5, PERSIST-040-O6, PERSIST-046-O1, PERSIST-046-O2, PERSIST-046-O3, PERSIST-046-O4, PERSIST-046-O5, PERSIST-046-O6, PERSIST-048-O1, PERSIST-048-O2, PERSIST-048-O3, PERSIST-048-O4, PERSIST-048-O5, PERSIST-054-O1, PERSIST-054-O2, PERSIST-054-O3, PERSIST-054-O4, UI-011-O1, UI-034-O1, UI-034-O2, UI-034-O3, WDL-007-O1, WDL-007-O2
- **SU-PROCESS-RECORDS: no Praxis process evidence of the bound kind** (24): AR-005-O1, AR-005-O2, AR-012-O1, AR-012-O2, AR-015-O1, AR-017-O1, AR-017-O2, AR-021-O1, AR-021-O2, PF-019-O1, UI-021-O3, UI-030-O3, UI-040-O2, VER-001-O1, VER-001-O2, VER-001-O3, VER-002-O1, VER-002-O2, VER-002-O3, VER-003-O1, VER-003-O2, VER-004-O3, VER-010-O1, VER-010-O2
- **SU-BROWSER-LIVE: tests/browser/live does not exist** (17): PERSIST-041-O1, PERSIST-041-O2, PERSIST-041-O3, PERSIST-041-O4, PERSIST-041-O5, PERSIST-049-O1, PERSIST-049-O2, PERSIST-049-O3, PERSIST-049-O4, PERSIST-049-O5, UI-006-O1, UI-006-O2, UI-006-O3, UI-006-O4, UI-006-O5, UI-006-O6, UI-006-O7
- **SU-BROWSER-WEBKIT: spec exists, bound title did not run** (16): PERSIST-030-O2, PERSIST-030-O5, PERSIST-030-O8, UI-011-O5, UI-016-O10, UI-016-O14, UI-016-O15, UI-016-O16, UI-016-O17, UI-016-O18, UI-016-O2, UI-016-O3, UI-016-O4, UI-016-O5, UI-016-O6, UI-016-O7
- **owner decision: no owner-approval record** (3): AR-008-O2, AR-024-O1, AR-024-O2
- **SU-VERIFIER: no independent verifier job** (2): PERSIST-026-O2, PERSIST-052-O3
- **failed bound test: `dotnet:Causa.Invariants.Property.INV04.RequestBounds` (DEV-R8-11, owner decision)** (1): PERSIST-016-O5

## Reproduce

```
python3 evidence/T34/raw/build-matrix.py evidence/T34/T34-matrix.json
python3 evidence/T34/raw/build-ledger.py
python3 <indy-init>/tools/validate-acceptance-ledger.py evidence/T34/ledger.json
REPORT=evidence/T34/T34-ACCEPTANCE-REPORT.md bash evidence/T34/raw/run-verdict.sh
```
