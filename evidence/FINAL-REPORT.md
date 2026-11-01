# Indy Init Rehearsal 8 — final report

- **Plan of record:** `indy-init-requirements-v4.1.1`
  - Plan commit f85081f; manifest sha256 77d15f55…
  - Approvals APR-V411-OWNER-1/2
- **Run shape:** topology C (role lanes, two UI agents: ui and ui-2), F#, autonomous execution with authorized PR merges.
- **Repository:** kemiller2002/indy-init-rehearsal-008. It already existed and the owner directed its use (DEV-R8-01/02). It was not overwritten, not deleted, and not created at T0.
- **Time window:**
  - T0: **2026-11-01T17:31:24Z**
  - Deadline: **2026-11-02T00:31:24Z** (420 min)
  - This report: written at about 23:55Z (T+384); the run stopped before the deadline.
- **Final candidate:** **`5e2717665b696a0c70175c7cacfc5702c5b2058d`**
  - Last implementation merge: #52, 23:05:51Z, T+334 (`c61bfe2`).
  - Two tests-only merges followed: #54 and #55, 23:30Z, T+359.
  - Merge freeze at 23:50:28Z (#56, evidence only).

## Timed verdict

**NOT RELEASABLE.** The acceptance verdict computed at the final candidate is **INVALID** (`tools/compute-acceptance-verdict.py`, exit 3; see `evidence/T34/T34-ACCEPTANCE-REPORT.md`).

**Why INVALID.** The pinned approvals file (`kickoff/rehearsal-007/approvals.causa.json`) fails the tool's trust checks:
- No approval names an immutable authority source (repository, commit, path).
- The changes are not in the required requirement/action form.

Only the owner can correct this; no agent writes approvals.

**With valid approvals, the ledger would still compute FAILED:**

| Gates | Status | Cause |
|---|---|---|
| G3 Domain | FAIL | INV04, DEV-R8-11 |
| G7 Defect class | FAIL | unwritten bound tests; INV04 awaiting the owner |
| G8 Candidate | FAIL | three required suites have no results |
| G0 Scope | BLOCKED | no requirements disposition file, so the preservation gate cannot run (exit 2) |
| G2 Interfaces | BLOCKED | 24 contract producer/consumer identities absent |
| G1, G4, G5, G6 | NOT_RUN | — |
| G9 Deployment | **PASS** | — |

## Actual results

### Bootstrap (T10)
- The `conditor init --preset indy-init` and v41 bootstrap finished in 20 s.
- The first green CI was at 17:35:57Z: T10 took 4 min 33 s against a 29 min plan. The remaining shared files were carried by T11.
- Details: `evidence/T10-RESULT.md`.

### Delivery
- 56 pull requests were opened.
  - 48 were merged by the coordinator through `merge.sh`, which does a local merge, reconciles Praxis `.ros`, builds, runs the contract and state-machine tests, runs `praxis validate`, and pushes.
  - The other 8 were carried inside the integration CR merges.
- Logs: `evidence/telemetry/merges.jsonl` (merges) and `evidence/SCHEDULE-ACTUALS.md` (schedule).
- **Delivered:**
  - DAG tasks T10–T31 across all lanes.
  - Review checkpoints T35, T36 and T30.
  - The T32 defect-closure set:
    - T32P: reconciliation past 250 commits, and on a failed last read.
    - T32W: wired commands carry their body, including T33-F-07.
    - T32L: read-back of files over 1 MB.
    - CR-T33-F08: WebKit storage probe.
  - The T33 fresh independent review.
  - T34 acceptance, at c61bfe2 and then at 5e27176.
  - Two tests-only sprints: ITESTS/DTESTS/PTESTS/UTESTS/ITESTS2, then FINALDET/FINALUI.
- **Product defects found by reviews and tests, and fixed in the window:**
  - live missing path (CR-T21-01)
  - compare truncation (T36 F-01)
  - exported expectedVersion (#41)
  - workspace blocker wording (#37)
  - reconcile stall past 250 commits, and a fault on a failed last read (#48)
  - wired domain commands recorded as Observations, so a connected investigation could never be activated (#51)
  - large-file read-back never confirming (#52)

### Verification at the final candidate 5e27176 (T34)

| Status (613 P0 obligations; the frozen plan has no P1) | 5e27176 | c61bfe2 | T31 b0ba767 |
|---|---|---|---|
| verified: every bound test passed at the candidate | **266** | 256 | 113 |
| failed | **1** (PERSIST-016-O5: INV04 RequestBounds, DEV-R8-11) | 1 | 0 |
| not run | **346** | 356 | 500 |

- Requirements with every obligation verified: **51 of 161**.
- **CI on 5e27176:**
  - ci: 268/268; invariants 34/35, INV04 failed.
  - Build and test: chromium 110/110, WebKit 92/92.
  - live: 61/61. Attempt 1 failed 3 tests on GitHub's primary API rate limit; they passed on the single allowed re-run, and both attempts are recorded.
  - deploy: green, including deployed-acceptance.
  - Praxis validation and Echelon foundations: green.
- **Evidence:**
  - Matrix: `evidence/T34/T34-matrix.json`
  - Ledger: `evidence/T34/ledger.json`
  - Report: `evidence/T34/T34-ACCEPTANCE-REPORT.md`
  - Fresh review: `evidence/reviews/T33-final-review.md`, with its findings dispositioned in `evidence/DEVIATIONS.md`.

## Unverified obligations (347 = 1 failed + 346 not run)

By cause (an obligation can have several):

| Cause | Obligations |
|---|---|
| SU-BROWSER-CHROMIUM spec file absent | 152 |
| SU-DOTNET-LIVE bound test is a bootstrap stub | 81 |
| SU-DOTNET-DETERMINISTIC bound test is a stub or absent | 45 |
| SU-BROWSER-CHROMIUM spec exists, bound title not run | 38 |
| SU-BROWSER-WEBKIT spec file absent | 33 |
| SU-PROCESS-RECORDS: no Praxis process evidence of the bound kind | 24 |
| SU-BROWSER-LIVE: `tests/browser/live` does not exist | 17 |
| SU-BROWSER-WEBKIT spec exists, bound title not run | 16 |
| Owner decision: no owner-approval record (AR-008-O2, AR-024-O1/O2) | 3 |
| SU-VERIFIER: no independent verifier job | 2 |
| Failed bound test: INV04 RequestBounds (DEV-R8-11) | 1 |

The full lists are in `evidence/T34/T34-ACCEPTANCE-REPORT.md`.

**Known gaps behind these counts (none is hidden by them):**
- **Missing behaviour:**
  - Opening an existing investigation from GitHub in a second tab. This blocks the PERSIST-040/044/046/050 specs.
  - The PERSIST-048 leave guard: Limen exposes no beforeunload.
  - Evidence source attribution (UI-017-O5, UI-031-O3).
  - Catalog-driven demonstration investigations in the browser suite (UI-001-O2..O5, UI-022-O3..O6).
  - Forma-workflow and Folio versions show "Not available".
- **Decisions needed:**
  - UI-004-O1 conflicts with "discoverable, never hidden": a requirements decision.
  - UI-016-O2..O7 exact hex colours: an owner decision.
- **Frozen-contract gaps reported to the owner:**
  - DEV-R8-09: IF41-DOMAIN has no command body. Worked around additively in #51.
  - DEV-R8-10, CR-T17-01.
  - DEV-R8-11: INV-04 contradicts SM-SAVE.
  - DEV-R8-12: SM-SAVE's failed final reconcile read.
- **P1 risk (T33-F-02):** the token is held as plain text in `HttpRequest.Headers` (a frozen wire shape). No leak path was found.
- **Remaining sweep items from T32P:**
  - The saving-path stale head past 250 commits ends in an unnecessary Conflict.
  - A marker commit with a missing file only ever offers Check again.
  - A 403 `too_large` answer is still a failed read.

## Deployment

**GitHub Pages**, through the approved workflow `.github/workflows/deploy.yml` (build → deploy-pages → deployed-acceptance), at https://kevinmmiller.us/indy-init-rehearsal-008/.

- **Final candidate 5e27176: deployed SHA verified.**
  - deployed-acceptance (run 41362398296, job 125015912543) logged `Deployed 5e2717665b696a0c70175c7cacfc5702c5b2058d`.
  - An independent curl check at 23:41:12Z confirmed:
    - the served `deployed-sha.txt` = 5e27176
    - `data-engine="loading"` present
    - `_framework/dotnet.js` returned 200
    - the Limen bundle returned 200
- **First verified deployment:** b45e969 at 19:40Z (T+129).
- Every push to main redeploys and re-verifies. Pushes after 5e27176 change only `evidence/` and `.ros/`, so they serve byte-identical application code. Their deploy runs re-check the SHA. The last of them, this report's commit, may still be deploying at the stop.

## Real provider behaviour

- **Live suite:** `live.yml` runs the provider suites against real GitHub. Reads go to `kemiller2002/causa-data` (CAUSA_GITHUB_TOKEN, read-only); writes go to `kemiller2002/causa-write-test` (CAUSA_WRITE_TOKEN, contents only). No agent ever read a secret value.
- **At the final candidate:** run 41362398338, attempt 2, passed 61/61. It made real commits on throwaway probe branches. The CI log records, for example:
  - PERSIST-043-O4: x-confirmed `4c7d864617743c40901a875da01b6de033a8d521`
  - PERSIST-041-O3: three tabs saving three investigations at once, `6d952a35…`, `e04c1596…`, `c5646d72…`
  - PERSIST-042-O4: other-writer stale head, `8de58471…`
  - PERSIST-007 ReadBackOfFileOver1MB: a file over 1 MB read back through one blob read
- **Earlier live history:** `evidence/LIVE-PROVIDER.md`.
- **Browser end to end:** the deployed page's Connect / Create / Save flow is covered in chromium against an in-memory GitHub (connect-save-flow, AR-035, UI-010, UI-023).
- **Not demonstrated:** a deployed-site browser run against real GitHub. That needs a personal token in a browser session, which this autonomous session does not hold, and SU-BROWSER-LIVE was never written (17 obligations not run). This item is unverified, not claimed.

## Token economics

Generated by `requirements-compiler token-economics` (docs/TOKEN-ECONOMICS.md) for the window 17:31:24Z–00:31:24Z. Files: `evidence/telemetry/token-economics.{json,md}`. Figures come only from Praxis `tokens.*` observations. Missing usage is reported as unavailable, never estimated.

- **Execution records:** 56. 48 have complete token data (all four buckets), 8 have none (unavailable), and none is partial.
  - The 8 with none: WI-R8-T10, CR2, CRT16, CRT23, CRT28, PAGES, CICOUNTS and CILIVE.
  - These are coordinator or CR executions that never had a transcript of their own; the coordinator session was ingested once, into WI-R8-COORD.
- **All executions, lower bound** ("partial" in the tool output):

  | Bucket | Tokens |
  |---|---|
  | input | 6,445 |
  | output | 2,151,093 |
  | cache read | 523,097,086 |
  | cache write | 8,568,478 |
  | **total** | **533,823,102** |

- **Timed executions (55):** 297,646,671 total.
- **Coordinator session (WI-R8-COORD):** 236,176,431.
  - It is reported in the tool's "unsplit (crosses the window boundary)" bucket because it began at 17:21:48Z, before T0, for the pre-T0 checks.
  - It was ingested once, at 23:50:44Z. Coordinator turns after that moment (this report's preparation) are not in the record.
- **By role:**

  | Role | Total tokens |
  |---|---|
  | integration | 89.9 M |
  | persistence | 60.8 M |
  | ui | 56.1 M |
  | domain | 35.2 M |
  | ui-2 | 30.8 M |
  | reviewer | 24.8 M |
  | coordinator | 236.2 M |

- **Model:** claude-opus-5-5 for all 48 executions with data.
- **Cost:** provider-reported cost unavailable; no price table supplied, so price-table cost unavailable.
- **Agent ingests:** every task agent ingested its own transcript exactly once, before `work complete`. The T34 re-run did not ingest a second time.

## Deviations

DEV-R8-01..03 are in `evidence/T0-START.json`; DEV-R8-04..12, the declined CRs (CR-T13-01, CR-T17-01, CR-T19-01) and the T33 dispositions are in `evidence/DEVIATIONS.md`.

**Owner directions:**
- use the 008 repository
- GitHub Pages is the target
- forma-workflow 1.2.0 approved as an exact pin
- FSharp.Core 10.1.402 central entry ratified

| Id | Summary |
|---|---|
| DEV-R8-01 | The pre-existing 008 repository was used as directed: not created at T0, not overwritten |
| DEV-R8-02 | `conditor repo create` refuses an existing repo, so a plain push was used instead |
| DEV-R8-03 | `conditor init` was run with GH_TOKEN/GITHUB_TOKEN unset |
| DEV-R8-04 | Central FSharp.Core 10.1.402 entry (ratified by the owner) |
| DEV-R8-05 | forma-workflow 1.2.0 (exact pin approved by the owner) |
| DEV-R8-06 | The invariant suite ran as a ratchet until T31 |
| DEV-R8-07 | Praxis 3.24.0 step identity breaks after ingest, so `praxis step` was not used |
| DEV-R8-08 | `.ros` merge reconciliation fallback (id/line union) |
| DEV-R8-09 | Frozen IF41-DOMAIN carries no command body (worked around with additive `decideOn` and a body lookup) |
| DEV-R8-10 | CR-T19-01 declined; ratchet exception |
| DEV-R8-11 | INV-04 contradicts SM-SAVE T-S31/T-S32 (unresolved, owner) |
| DEV-R8-12 | A failed final reconcile read in SM-SAVE is mapped to the T-S30 effects (ratified, reported) |

## Elapsed time

| Milestone | Time | Elapsed |
|---|---|---|
| T0 | 17:31:24Z | 0 |
| first green CI | 17:35:57Z | 4.5 min |
| first verified deployment | 19:40Z | 129 min |
| first release candidate, T31 | 21:53Z | 262 min |
| last implementation merge | 23:05:51Z | 334 min |
| tests-only freeze, final candidate | 23:30Z | 359 min |
| merge freeze | 23:50:28Z | 379 min |
| this report | about 23:55Z | about 384 min |

- Implementation stopped before the 420-minute hard stop, and evidence is preserved on main.
- No further requirements-development cycle was started.
