# T36: Independent review checkpoint 2 (live write path and conflict handling)

- Reviewer: claude-code/reviewer (WI-R8-T36). Independent of the T19, T20, T21 and CRT2101 authors. Findings come from code, the package and CI logs, not from PR descriptions (AR-008).
- Covers: AR-034 (O1 try to falsify the write path and acceptance claims before the first release candidate; O2 spot-check that bound tests prove the whole obligation). The completion items come from v4 curation/tasks.json T36 (DCC-003; critical findings have an owner and are fixed or decided before the next checkpoint).
- Reviewed at: `main` 385f2a5 (T21 and T25 merged); PR #23 `origin/r8/crt2101` 1bb31fd (CR-T21-01, the R-07 absent-file rule, not merged); live.yml runs 41341530987, 41341841238, 41342268099 (main) and 41342363820 (main 12fcff7, still in progress when this was recorded).
- Time: 19:33Z to 19:55Z (T+122 to T+144).

## Verdict: FAIL ON MAIN, PASS WITH FINDINGS ONCE PR #23 MERGES

- **Blocker on main.** The live write path does not work on main. live.yml on 385f2a5 passed 16 of 45 tests: 29 failed with `OpenRefused (NotFound …)`, so no investigation can be created. Every green live run came from a branch that is not main. Merging PR #23 removes the blocker; it builds clean and its tests pass (below).
- In the persistence library, the write machinery is sound:
  - Each save sends one createCommitOnBranch with `expectedHeadOid`.
  - Confirmation is built only from byte-equal read-back.
  - The retry bounds come from the matrix.
  - Every SM-SAVE and SM-CONFLICT pair is either legal or fails closed.
- One major hole lets a rebase write over another writer's change (F-01).
- The application does not yet use the save or conflict machines (F-03).

## Commands run (evidence)

| Command | Tree | Result |
|---|---|---|
| `dotnet build Causa.slnx` | main 385f2a5 | 0 warnings, 0 errors |
| `dotnet test tests/Causa.Persistence.Tests` (CAUSA_DATA_ROOT=/home/user/causa-data) | main | Passed 96/96 |
| `dotnet test tests/Causa.Contract.Tests` | main | Passed 15/15 |
| `dotnet test tests/Causa.Invariants` | main | 31 passed, 5 failed, 0 skipped. INV07, INV12 and INV14 fail RED BY DESIGN (ui Engine/Projections not bound). Live.INV22 is BLOCKED (no write token locally). Property.INV04 fails with counterexample seed 1, reads at 2000, 6000, 14000, 37645, 53675 ms: five reconciliation reads inside 60 s |
| `dotnet build Causa.slnx` | r8/crt2101 1bb31fd | 0 warnings, 0 errors |
| `dotnet test` Persistence, Contract, Invariants | r8/crt2101 | 96/96, 18/18 (3 new R-07 producer tests), Invariants the same 5 failures as main |
| `dotnet fsi` probe (scratchpad probe36.fsx) on the crt2101 build | r8/crt2101 | results quoted in F-01, F-05 and F-06 |
| live.yml logs, run 41342268099 (main 385f2a5) | CI | **16/45 passed, 29 failed**, each `LiveProvider "kemiller2002/causa-write-test": expected an empty head, got OpenRefused (NotFound …)` (PERSIST-005-O5, PERSIST-021-O2, PERSIST-041-O3, PERSIST-043-O3, …) |
| live.yml logs, run 41341530987 (r8/crt2101 116f1ad, workflow_dispatch) | CI | 34/34 passed. **No PERSIST-041/042/043 line in the log**: this branch predates the T21 PAT suites |
| live.yml logs, run 41341841238 (r8/crt2101-t21-probe 7ef51e4 = crt2101 merged with T21) | CI | 45/45 passed, with PAT-02/03/04 lines `provider=live-github repository=kemiller2002/causa-write-test` and commit SHAs (below) |

## 1. Live write path (code on main, plus PR #23)

- **One commit per save, with the expected head.**
  - `Session.fs:104-123` `sendMutation` sends one `createCommitWithTrailer` with the head it was given. It writes `BaseHead = expected` and counts `Mutations`.
  - Bounds: `maxRebases = maxAttempts GhStaleHead` (3), `maxMutations` 4, `maxAutomaticReads = maxAttempts GhNetworkAfter` (3) at `Session.fs:32-34`. These close T35's R-11.
  - The live log for PERSIST-042-O1 (run 41341841238) shows tab A's single mutation refused with GH-STALE-HEAD. `head-after=eded987…` equals the other writer's commit, so nothing was overwritten.
- **Read-back confirmation only from matching bytes.**
  - `Session.fs:360-381`: manifest, events and snapshot are re-read at the returned commit one at a time. Each is compared byte-for-byte with the bytes written, and `Confirmation.ofReadBack` (private constructor, `SaveMachine.fs:21-22`) is reached only after all three match.
  - The reconciliation path (`ReconcileFilesAt`) reuses the same check, and a marker match alone never confirms.
- **Conflict never auto-resolved.**
  - A stale head goes FreshHead → compare → StaleHeadSameInvestigation (conflict) or StaleHeadUnrelated (rebase).
  - Unknown, missing or truncated commit lists count as changed (`Session.fs:390-393`).
  - SM-CONFLICT (`Conflict.fs:66-99`) needs an explicit Reapply, or Discard followed by DiscardConfirmed. Every other pair fails closed.
  - Exception: F-01, where the *file* list is truncated.
- **Every pair legal or fail-closed.** `step` ends in `| _ -> fault …` (`Session.fs:324`, `Conflict.fs:99`). `tests/Causa.Persistence.Tests/StateMachines/SMSAVE.fs`, `SMCONFLICT.fs` and `SMLOAD.fs` drive pairs from state-machine-contracts.json, and all pass.
- **Classifier precedence** (`Classify.fs:255-333`): AUTH, RATE-PRIMARY, RATE-SECONDARY, rate-limit message (R-03), STALE-HEAD, LARGE-FILE, PERMISSION, 404 by context, FORBIDDEN, VALIDATION, NOT-FOUND, then the rest.
  - No 200 is accepted when it carries GraphQL errors.
  - Stale head is decided from the error content.
  - `meaning` (`Classify.fs:345-357`) separates an unknown mutation outcome from a failed read (T35 R-05).
- **PR #23, the R-07 absent-file rule** (`r8/crt2101` Classify.fs:174-212 and :316-323).
  - The rule applies only in a `Read` context. It drops an error only when all of these hold:
    - the error type is NOT_FOUND;
    - the message is exactly "Could not resolve file for path '…'";
    - the answer names the commit oid;
    - the field the error names answered null.
  - Mutation, Connect and Compare keep every error.
  - A missing branch or ref has no commit oid, so it is still GH-NOT-FOUND.
  - A partial file set fails closed as Incompatible (producer tests).
  - This is a correct reading of the matrix: GraphQL GH-NOT-FOUND names a missing ref, not a missing path inside a commit that was found. Residual: F-05.

## 2. Live evidence

- Run 41341841238 prints `provider=live-github repository=kemiller2002/causa-write-test` with commit SHAs for each live obligation:
  - PERSIST-041-O3: tab1/2/3 confirmed 8e508f2…, 086f1fc…, a6f6fdf….
  - PERSIST-042-O1, O2 and O4: other-writer equals head-after, so the stale tab wrote nothing.
  - PERSIST-043-O1 through O5: x-read, same-tab-y, other-writer-outside and x-confirmed are four distinct commits.
- Each run uses a fresh `probe/t21-*/<run>-<n>` branch.
- The fake half of each test prints `provider=stateful-fake (NOT live verification)`. That labelling is honest.
- I could not open the commits in causa-write-test from this session: that repository is not attached. The SHAs are verified only as printed by CI.
- Recordings: the new live form in PR #23 is a `DocumentedForm` whose source says "the run printed the messages, not the raw body … not a capture". No `CapturedLive` entry exists. Labelling is honest.
- **The claims are only partly correct** (F-02, F-04):
  - "34/34 including PAT-02/03/04" is wrong. Run 41341530987 has no PAT test.
  - Neither green run is main. Main's latest completed run is red (16/45).

## 3. T35 follow-ups

| T35 | Status | Evidence |
|---|---|---|
| R-01 context-free classifier binding | fixed | INV22 binds `Require.classifier ()` with the answer context (DEV-R8-10) |
| R-02 INV22 not all forms | fixed | INV22 is Recordings-driven, plus context forms (`INV22.fs:111-112`, …) |
| R-03 403 rate-limit message → PERMISSION | **fixed** | `Classify.fs:295-298` maps it to GH-RATE-PRIMARY. Tested in `Contract.Tests/Errors/Producer.fs:44` and in INV22 "403 rate-limit message, no rate headers" and "remaining 5" |
| R-04 compare total_commits throws | fixed | `Classify.fs:220-228` uses TryGetInt64 |
| R-05 fallback by context | mostly fixed | `meaning` added. Residual F-06 (mutation 404) |
| R-06 fabricated oid / read without content | fixed | stale head only in a Mutation context; a read without content is GH-MALFORMED (`Classify.fs:240-252`) |
| R-07 INV-11 storage mutants | **fixed** | `INV11.fs:139-254`: schema-valid snapshot forgery, exact refusals, `EachIntegrityCheckHasAMutant` per check. Passes on main |
| R-08 tautological replay mutant | fixed | exact `EmptyHistory` / `NonContiguousVersion` expected |
| R-09 INV-04 bounds | **decided, not met** | DEV-R8-10/11: INV-04's 4-per-60 s cap contradicts T-S31/T-S32, which send a read immediately on Check again or going online; the machine has no clock. Reproduced: five reads in 60 s. Coordinator raised it to the owner, and INV-04 / PERSIST-016 stay unverified. I accept the decision, since a clock or a table change is an owner change. It stays an open obligation, not a pass |
| R-10 INV-12 Unsaved branch | fixed | `INV12.fs:60-63` requires `refused = []` |
| R-11 retry bounds unused | fixed | `Session.fs:32-34` |

## Findings

| ID | Severity | Where | Owner | Requirement |
|---|---|---|---|---|
| F-01 | **major** | `src/Causa.Persistence/Session.fs:390-393`, `Reconcile.fs:89-97` | persistence | PERSIST-011, PERSIST-042/043, "never overwrite another writer" |
| F-02 | **blocker (main)** | main 385f2a5 lacks PR #23; live.yml run 41342268099 | integration | PERSIST-005/021/041/042/043 live |
| F-03 | **major** | `src/Causa.App/Engine.fs` (types only), `Update.fs` (2 lines); `Conflict.fs:93-94, 98` | ui / integration | SM-CONFLICT T-C04, T-C12, T-C13; PERSIST-012 |
| F-04 | minor | live evidence claims; `tests/Causa.Acceptance/Live/*` fake log lines | integration | AR-034-O2 (claims prove what they say) |
| F-05 | minor | `r8/crt2101` `src/Causa.Persistence/Classify.fs:174, 202-212` | persistence | R-07 rule, GH-NOT-FOUND |
| F-06 | minor | `Classify.fs:310`; `Session.fs:380` | persistence | GH-UNKNOWN-OUTCOME, PERSIST-023 |

### F-01 (major) A truncated compare *file* list is taken as complete, so a rebase can overwrite another writer's change to this investigation

- **Cause.** GitHub's compare lists at most 300 changed files for the whole comparison and gives no truncation flag. `Answers.compare` keeps whatever `files` arrived. `investigationChanged` checks only `Total = Commits.Length` (commit truncation) and then searches that list.
- **Repro (probe36.fsx).** A compare body with `total_commits: 1`, one commit and 300 files, none under the investigation, decodes to `Files = Some [300 names]`, `Total = 1`, `Commits = 1`. `investigationChanged` returns false and `interpret` emits `StaleHeadUnrelated`, which rebases (`Session.fs:441-443`). The reconciliation path does the same at `Session.fs:466-471` (`ReconcileNothingWrittenHeadMoved`, then rebase).
- **Failure scenario.**
  1. Another writer (a bulk import, a script, or another Causa tab saving many cases) makes one commit that touches more than 300 files, including this investigation's three.
  2. The tab's guarded mutation gets GH-STALE-HEAD.
  3. The compare lists 300 other files, so the tab decides the change is unrelated.
  4. It re-sends its file set on the fresh head. GitHub accepts it, and the other writer's version of this investigation is replaced without a conflict.
- **Fix.** Treat `Files.Length >= 300` as unknown, which counts as changed. Better: decide "unrelated" by reading the investigation's three blob oids at the base and at the fresh head (one GraphQL query) instead of trusting the file list. Add a stateful-fake case with more than 300 files.

### F-02 (blocker on main) Main's live write path fails: 29 of 45 live tests fail with OpenRefused NotFound

- **Evidence.** Run 41342268099 on 385f2a5 (after PR #19 merged T21): 16 passed, 29 failed. Every failure is the missing-path read that PR #23 fixes.
- **Failure scenario.** A user who connects to an empty repository or opens a new investigation on main gets "not found" and cannot create anything.
- **Fix.** Merge PR #23, which builds clean and passes the tests above. Then require a green live.yml on a main SHA before the T21/T30 live obligations are marked verified. Branch run 41341841238 is evidence for 7ef51e4, not for main.

### F-03 (major) The application does not drive SM-SAVE or SM-CONFLICT, and the engine duties the machines delegate do not exist

- **What exists.** `Engine.fs` declares `SessionInput` and `ConflictStep`, but no code under `src/Causa.App` or `src/Causa.Host` calls `SaveProcess.create`, `interpret`, `step`, `ConflictMachine.readRequest` or acts on `ExportCommands`. The live write path exists only as the persistence library driven by tests.
- **Delegated duties left undone.**
  - `Conflict.fs:93-94` (T-C04, T-C13): RemoteUnavailable/Unresolvable + ReadAgain returns `ReadingRemote id` with **no Send**, because the frozen view has no investigation id. The engine is meant to send the read.
  - `Conflict.fs:98` (T-C12): emits `ExportCommands []`, which the engine is meant to fill.
- **Failure scenario.** Once the UI is wired, any integration that just runs the effects shows "Reading GitHub…" forever after Read again, and Export downloads an empty file while the user's unsaved commands sit in the session.
- **Fix.** The ui/integration owner wires the engine, sending `readRequest` with the recorded RequestId and filling the export from `Unsaved @ AfterInFlight`. Add an Engine-level test for T-C04, T-C12 and T-C13. The CR-T19-01 decline (DEV-R8-10) makes this an engine obligation, so it must be tracked as one.

### F-04 (minor) Live evidence claims are worded beyond what the runs show

- **Evidence.**
  - Run 41341530987 (34/34) contains no PERSIST-041/042/043 test; `grep -c PERSIST04[123]` = 0. Only run 41341841238 (45/45) proves PAT-02/03/04 live.
  - The fake half prints `branch=main` (for example `PERSIST-041-O3 branch=main … provider=stateful-fake (NOT live verification)`). It is labelled, but a grep for `branch=main` finds fake lines.
- **Fix.** Cite only 41341841238 (and later main runs) for PAT live evidence. Print `branch=<fake>` for the fake half.

### F-05 (minor) The R-07 rule does not tie the error's path text to the path that was read

- **Repro.** On crt2101, a 200 whose data names commit `abc1234` with a null `manifest` field, plus an error "Could not resolve file for path 'some/other/path.json'." pointing at that field, classifies as **Accepted** (absent file).
- **Impact.** Low: the field the error names answered null, so the absent-file reading is still true. But the message is not checked against the query's variables.
- **Fix.** Compare the quoted path with the requested `StoragePaths` for that slot, or drop the message regex and rely on `path` plus null.

### F-06 (minor) Two refusals carry the wrong meaning

- **Mutation 404.** `classify (Mutation h) (Answered(_,404,[],"{}"))` gives `GhNotFound` with meaning `Definite` (`Classify.fs:310`), and the user is told "Save failed: the branch was not found". The matrix's NOT-FOUND row is a REST read or a GraphQL NOT_FOUND error. A 404 from the GraphQL endpoint is outside the matrix, so it should be GH-UNKNOWN-OUTCOME, or at least not "branch not found". The outcome stays fail-safe: nothing is confirmed.
- **Read-back 404.** A 404 on the read-back contents read maps to `Differ` (`Session.fs:380`), which becomes `ReadBackMismatched`, which gives "GitHub holds different bytes" (SaveFailed BytesDiffer). If the contents endpoint briefly 404s on a commit that was just created, the save is reported as different bytes instead of ReadBackReadFailed (UnknownOutcome, then reconcile). This is fail-safe, since it is never confirmed, but the cause shown is wrong and no automatic reconciliation runs.

## Obligations status (AR-034)

- **AR-034-O1: met.** The write path and the live acceptance claims were attacked at T+122..144, before any release candidate. F-01 and F-02 are falsifications.
- **AR-034-O2: met, by spot-check.** PERSIST-042-O1/O2 and PERSIST-043-O1..O5 assert the expected head, the class and the head-after against production `classify` and `SaveProcess`, on both providers. INV-04 is correctly left unverified (DEV-R8-11). PERSIST-011, "never overwrite on a guess", is only partly proven: no test covers a truncated file list (F-01).
- **T36 completion 1 (DCC-003): met.** Each finding has a defect class, file:line, a reproduction or failure scenario, and an owner.
- **T36 completion 2: open.** F-02 needs PR #23 merged plus a green main live run. F-01 and F-03 need an owner fix or a decision before T30.
