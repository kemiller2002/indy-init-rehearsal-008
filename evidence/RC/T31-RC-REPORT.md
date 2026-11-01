# T31 release candidate report: full applicable matrix at the deployed SHA

- Work item: WI-R8-T31 (integration lane). Generated 2026-11-01 ~22:05Z.
- **Candidate: `b0ba76755956e052f6d6291266fd2ca481b34f12`** (main at T31 start, merge of PR #40).
- Plan: v4 `VP-1e3c66bcc958` (613 obligations, all P0, 0 P1), executed per `requirements-compiled/v4.1.1/verification-execution-plan.json` (11 suites).
- Machine-readable matrix: `evidence/RC/T31-matrix.json` (one row per obligation: its bound test identities, each test's suite, owner, result and evidence URL). Parsed raw results and the scripts that produced the matrix are in `evidence/RC/raw/`. Re-run with `python3 evidence/RC/raw/build-matrix.py` (after the inputs listed in `raw/sources.json` are in place).

## Verdict

**Not releasable at this SHA.** 113 of 613 P0 obligations are verified. 1 bound test fails: INV04 RequestBounds, a known package contradiction (DEV-R8-11). 500 obligations are not run, mostly because their bound acceptance tests were never written (the bootstrap stubs are still in place). No obligation is counted as verified unless every test bound to it passed at the candidate SHA.

## Counts (P0; there are no P1 obligations in the frozen plan)

| Status | Obligations |
|---|---|
| verified (every bound test passed at the candidate SHA) | **113** |
| failed (a bound test failed, excluding recorded by-design cases) | **0** |
| not run | **500** |
| unverified by design, as the sole blocker | **0** (see the note below) |
| **total** | **613** |

Requirements whose every obligation is verified: 18 of 161 (EXT-DATA-013, PERSIST-005/006/008/021/025/032/042/043, UI-014/018/019/020/029, VER-007/008, WDL-001/002).

Breakdown of the 500 not-run obligations:
- 428: at least one bound automated test produced no result (the test is missing at the candidate).
- 72: bound only to a process or review record (Praxis `independent-review`, `owner-approval` or process evidence), and none exists yet at the candidate.

**Unverified by design (annotated).** These 4 obligations are also blocked by something other than the by-design cause, so they are counted under not-run:
- PERSIST-016-O5: `dotnet:Causa.Invariants.Property.INV04.RequestBounds` **FAILS** at the candidate (DEV-R8-11: INV-04 contradicts SM-SAVE T-S31/T-S32; owner decision pending). Its acceptance test `PERSIST016Tests.PERSIST-016-O5` is also a stub.
- AR-008-O2, AR-024-O1, AR-024-O2: these need an owner-approval record (owner decision). They are also bound to the T33 independent review.
- DEV-R8-09 and DEV-R8-10 (frozen-contract gaps) do not appear as a failing bound test in this run. Their effect is absorbed in implementation (Kernel.decideOn, SaveSetup injection), so no obligation is classed by-design for them. Live-only obligations ran in CI (live.yml) where a test exists.

Test identities in the execution plan (660 plan tests plus 140 own tests):

| Suite | Environment | Planned | Pass | Fail | Not run | Evidence |
|---|---|---|---|---|---|---|
| SU-OWN-DOTNET | ci | 137 | 113 | 0 | 24 (contract tests absent from source) | ci.yml job 124991051909 |
| SU-OWN-BROWSER | ci-browser | 3 | 3 | 0 | 0 | build-and-test job 124991051944 |
| SU-DOTNET-DETERMINISTIC | ci | 116 | 4 | 0 | 112 (25 of 27 files are bootstrap stubs) | ci.yml job 124991051909 |
| SU-DOTNET-INVARIANTS | ci | 21 | 20 | **1** (INV04) | 0 | ci.yml job 124991051909, ratchet step |
| SU-BROWSER-CHROMIUM | ci-browser | 281 | 72 | 0 | 209 (169: spec file absent; 38: spec exists but the title did not run; 2: identity drift) | build-and-test job 124991051944 |
| SU-BROWSER-WEBKIT | ci-browser | 72 | 22 | 0 | 50 (33: spec absent; 17: titles not run in the CI WebKit job) | build-and-test job 124991051691 |
| SU-DOTNET-LIVE | live-github | 141 | 45 | 0 | 96 (stub files) | live.yml job 124991052003 |
| SU-BROWSER-LIVE | deployed-live | 17 | 0 | 0 | 17 (`tests/browser/live` does not exist) | none |
| SU-VERIFIER | independent | 2 | 0 | 0 | 2 (no independent verifier job) | none |
| SU-REVIEW-RECORDS | process | 2 | 0 | 0 | 2 (T33 review pending; owner approval) | none |
| SU-PROCESS-RECORDS | process | 8 | 0 | 0 | 8 (no Praxis evidence of these kinds in `.ros`) | none |

Every CI test that ran on the candidate passed except INV04. The totals: ci.yml non-live .NET 211/211 plus invariants 34/35; chromium 95/95; webkit 76/76; live 46/46.

## CI runs on the candidate (primary evidence)

| Workflow | Run | Result |
|---|---|---|
| ci.yml (Causa CI) | https://github.com/kemiller2002/indy-init-rehearsal-008/actions/runs/41354331805 | success. The invariant step is continue-on-error (DEV-R8-06) and failed with 1/35. T31 requires every invariant to pass, so this gate is **not met**. |
| build-and-test.yml | https://github.com/kemiller2002/indy-init-rehearsal-008/actions/runs/41354331812 | success (chromium 95 passed, webkit 76 passed) |
| live.yml | https://github.com/kemiller2002/indy-init-rehearsal-008/actions/runs/41354331822 | success (46 live tests against real GitHub) |
| deploy.yml | https://github.com/kemiller2002/indy-init-rehearsal-008/actions/runs/41354331835 | build and Pages deploy: success. **deployed-acceptance: failure** on attempts 1 and 2 (D-T31-01 below) |
| candidate-acceptance.yml (dispatched by T31, candidate pinned) | https://github.com/kemiller2002/indy-init-rehearsal-008/actions/runs/41354446821 | success (pin and verify). Note: it runs App.slnx and the chromium project, not the plan's suite matrix. |
| praxis-validation | https://github.com/kemiller2002/indy-init-rehearsal-008/actions/runs/41354331819 | success |
| echelon-foundations | https://github.com/kemiller2002/indy-init-rehearsal-008/actions/runs/41354332124 | success |

## Deployed SHA verification

- Pages URL: https://kevinmmiller.us/indy-init-rehearsal-008/
- In deploy.yml's deployed-acceptance job, the SHA loop passed: the served `deployed-sha.txt` equalled `GITHUB_SHA`, and the job did not print "Deployed SHA ... is not ...". The step then failed on the next line with curl exit 23 (see D-T31-01).
- Independent check by T31 at 21:57Z (`evidence/RC/T31-deployed-check.log`): the served `deployed-sha.txt` is `b0ba76755956e052f6d6291266fd2ca481b34f12`, matching the candidate. `index.html` carries `data-engine="loading"`. `_framework/dotnet.js` and `limen dist/index.js` return 200.
- **The deployed SHA equals the candidate.** The deployed-live browser suite (SU-BROWSER-LIVE) cannot run because its tests do not exist.

## Failing list (with owners)

| Test | Obligation | Owner | Note |
|---|---|---|---|
| `dotnet:Causa.Invariants.Property.INV04.RequestBounds` | PERSIST-016-O5 | test file: domain; resolution: owner decision (DEV-R8-11) | counterexample (seed 1): more than four reconciliation reads in 60 s under total network failure |

Bound tests that are not run, by owner of the test file per `agent-file-ownership.json`:

| Owner | Count |
|---|---|
| ui | 156 (133 chromium, 23 webkit) |
| integration | 218 (81 live .NET, 60 deterministic .NET, 36 chromium, 21 webkit, 10 deployed-live, 8 process records, 2 verifier) |
| persistence | 108 (42 deterministic, 40 chromium, 13 live, 7 deployed-live, 6 webkit) |
| domain | 12 (10 deterministic, 2 live) |
| reviewer | 1 (`praxis:independent-review`, T33) |
| owner | 1 (`praxis:owner-approval`) |

The per-test list is in `T31-matrix.json` (`obligations[].tests[]` where `result` is not `pass`).

## Candidate defects for T32

1. **F-03 (verified, open): exported commands carry `expectedVersion: null` after a save.** `src/Causa.App/Program.fs:356` builds every DomainCommand envelope with `ExpectedVersion = None`. `Update.fs:508` stores it unchanged in the session (`SaveInput.Edit envelope`). `Update.exportJson` (`Update.fs:49-51`) serializes the stored envelope, so an observation recorded after Create saved (base version ≥ 1) exports `"expectedVersion": null`. `decideAll` computes `decidedOn` (`Update.fs:195`) but only for the decision; export never uses it. The browser spec `tests/browser/contract/connect-save-flow.spec.js:356` accepts `null` or an integer, so the defect is not caught. Fix owners: ui (Program.fs envelope, or export of the decided version) and integration (tighten the spec).
2. **D-T31-01 (fixed in this PR): deploy.yml deployed-acceptance always fails.** `curl -fsS index.html | grep -q ...` under `set -o pipefail`: `grep -q` exits at the first match, curl gets EPIPE (exit 23), and the step fails although the page is right. Reproduced locally (old form exits 23; the new form exits 0). The fix is `grep ... > /dev/null`, which reads the whole stream. It is integration-owned and one line. It takes effect at the next main SHA, whose deploy run must show deployed-acceptance green.
3. **INV04 RequestBounds fails** (DEV-R8-11; package contradiction, owner decision). The ci.yml invariant step is still continue-on-error, so CI is green while T31's "every invariant passes" gate is red.
4. **Acceptance test bodies missing (defect class: unwritten bound tests).** 25 of 27 `tests/Causa.Acceptance/Deterministic/*.fs` and 34 of 44 `Live/*.fs` files are the requirements-compiler bootstrap stub (2 lines). 202 bound chromium/webkit tests are in spec files that do not exist, and 55 more bound titles do not run from spec files that do exist. `tests/browser/live/` does not exist (17 deployed-live tests).
5. **Own interface-conformance tests missing:** 24 of 137 SU-OWN-DOTNET identities do not exist in source, for example `Causa.Contract.Tests.Save.Producer.*` (6), `Wire.Producer.*` (3), `Storage.Producer.GoldenFixturesRoundTrip` and `EachIntegrityCheckRefusesItsForgery`, `Domain.Consumer*.KernelSignaturesAndFixtures` (4).
6. **Test identity drift:** `tests/browser/invariants/INV-06.spec.js` and `INV-10.spec.js` run as "INV-06" and "INV-10" instead of the plan identities "every store empty of investigation content" and "tabs independent with and without navigator.locks". INV-10 does not exercise the without-`navigator.locks` case. Both are counted not-run.
7. **CI WebKit coverage gap:** build-and-test's WebKit job runs `tests/browser/acceptance` and one contract spec, not `tests/browser/invariants` (plan SU-BROWSER-WEBKIT includes INV-24, whose spec is also absent).
8. **No independent verifier job (SU-VERIFIER) and no Praxis process or review evidence records** (`work evidence --kind ...`) in `.ros`. 72 obligations depend only on these records.

## Method and limits

- Results come only from CI at the candidate SHA (TRX artifacts for .NET; the Playwright list reporter in the job logs for the browser suites, since the json report is uploaded only on failure). Nothing was estimated. A test identity counts as passed only when that exact identity ran and passed.
- No local suites were run. Every suite that has tests ran in CI at the candidate. The remaining not-run identities have no test to run.
- P1: the frozen plan has no P1 obligations.
