# T30: Independent architecture and invariant review before the first release candidate

- Reviewer: claude-code/reviewer (WI-R8-T30). Independent of every implementing lane. Findings come from code, the
  v4.1.1 package and CI logs, not from PR descriptions or authors' summaries (AR-008).
- Covers: `doc:docs/DESIGN-DECISION-BOUNDARIES.md`, DCC-002, AR-008. Completion (v4 curation/tasks.json T30): findings
  classified by defect class with a reproduction (DCC-003).
- Reviewed at: `origin/main` **1a40163** (PR #28 merged). Open PRs #29 (r8/t22b 9630e43) and #25 (r8/t26) are not on
  main and are not reviewed, except where noted.
- Time: 20:02Z to 20:15Z (T+151 to T+164).

## Verdict: NOT READY FOR RC. The libraries pass; the product path in the browser does not exist yet

- The parts reviewed in isolation are sound:
  - frozen contracts are intact;
  - the engine is pure;
  - the CSS uses only Forma tokens;
  - the state machines cover every pair;
  - live CI is green on main.
- **Blocker (F-01).** The deployed page cannot connect, open or save. The engine sends no effects to the browser,
  ignores effect results, and no `Engine` (IF41-APP) is implemented. INV07 and INV12 are therefore red, and every
  PERSIST browser obligation and PAT scenario at the deployed SHA is NOT_RUN. T31 cannot pass until this is closed.
- **Major (F-02).** CI never publishes the invariant counts, so the DEV-R8-06 ratchet cannot be enforced from CI.

## Commands run (evidence)

| Command | Tree | Result |
|---|---|---|
| `dotnet build Causa.slnx` | 1a40163 | 0 warnings, 0 errors |
| `dotnet test tests/Causa.Persistence.Tests` (CAUSA_DATA_ROOT=/home/user/causa-data) | 1a40163 | 103/103 |
| `dotnet test tests/Causa.Contract.Tests` | 1a40163 | 30/30 |
| `dotnet test tests/Causa.Domain.Tests` | 1a40163 | 17/17 |
| `dotnet test tests/Causa.App.Tests` | 1a40163 | 13/13 |
| `dotnet test tests/App.Engine.Tests` | 1a40163 | 5/5 (bootstrap template engine) |
| `dotnet test tests/Causa.Invariants` | 1a40163 | **32 passed, 4 failed, 0 skipped**; details below |
| `dotnet test tests/Causa.Acceptance` | 1a40163 | 7 passed, 40 failed. Local environment only: 38 of the 45 Live tests need a token this session does not have (BLOCKED), and the 2 VER-008 tests fail because the worktree has no `node_modules` (`BLOCKED … installed missing`). Not product defects |
| `python3 t30-manifest.py` (SHA-256 of every spec plus a line-by-line containment check against the producer) | 1a40163 | 9/9 spec hashes match `evidence/interface-manifest.json`; every producer contains its spec verbatim (IF41-DOMAIN without its `namespace` line); the manifest file's own sha256 is 3018e1dd…201ce, as DEVIATIONS records |
| state-machine coverage script (contract pairs against test names and legal sets) | 1a40163 | every transition test exists; legal sets equal the contract for SM-SAVE (50), SM-CONFLICT (15), SM-CONNECTION (9) and SM-LOAD (9); SM-NETWORK is checked by T-W01/T-W02 plus an illegal-pair test (F-05) |
| ownership diff per merged PR (agent-file-ownership.json, topology C) | 1a40163 | see §4 |
| `gh run view` 41344722346 (Causa CI), 41344722307 (Build and test), 41344722282 (live), deploy 41344722293 | 1a40163 push | all green. Live: **45/45** on a main SHA. Browser: 41 + 50 Playwright passed. Invariant step: **no counts printed** (F-02) |
| `npx playwright test` locally | — | not run: the worktree has no `node_modules` or site build. CI run 41344722307 on the same SHA is the evidence |

## 1. Architecture against the approved stack (F#, Limen, Forma, Folio; AGENT-RULES v4.1.1)

- **Engine purity: holds.**
  - No browser API, HttpClient, clock or `JSImport` in any `src/**/*.fs`.
  - There are two mutable cells, both at the edge: `Program.fs:133` (the Limen dispatch state) and
    `Reconcile.fs:159-161` (the tab's write-marker counter).
  - `Guid.NewGuid` is called only in `Session.fs:507` `forNewTab`, the test and invariant factory.
  - `Causa.Host/Host.cs` is marshalling only (`Dispatch` → `Program.dispatch`) and holds no decision.
- **Frozen contracts: intact** (re-verified above).
- **Additive types do not shadow frozen ones.**
  - Duplicate type names in `src`: `T` (module-scoped in Wire.fs and SaveMachine.fs) and `Route`. One `Route` is
    `Causa.App.Engine.fs:56`, the frozen one; the other is in the bootstrap template `src/engine/Routes.fs`, a
    separate project.
  - The declined change requests (DEV-R8-09/10, CR-T17-01) are implemented as functions beside the frozen blocks
    (`Kernel.decideOn`, `Update.submit`).
- **CSS: holds.**
  - `web/causa.css` declares no custom property and has no hex, rgb, px, rem or ms literal.
  - Its 16 `var(--ef-…)` tokens all appear in `analysis/forma-0.12.0-inventory.json`.
- **Dialog invokers:** there is no `<dialog>` and no popover in `web/index.html` yet, so nothing is wrong today. The
  conflict "Discard" confirmation (T-C, DiscardConfirmed) and the Disconnect confirmation (SM-CONNECTION, "confirmation
  was asked") will need a dialog with `commandfor`/`aria-controls` when they are wired. That belongs to T31's
  layout-test pass.
- **Folio:** only the bootstrap `src/kernel/print.html` references print-components. `Views/Report.fs` is the 2-line
  bootstrap stub (T26 is PR #25, not merged).
- **D-009 persistence truth: holds today.** The tab-local investigation shows "Unsaved … only in this tab"
  (`Views/SaveIndicator.fs:22-36`) and never "Saved".

## 2. Invariants (non-live, `dotnet test tests/Causa.Invariants`)

| Test | Result | Class | Owner |
|---|---|---|---|
| Negative.INV07 RefreshNeverReplacesWork | RED BY DESIGN: `Bindings.engine = None` (`INV08.fs:39`) | design-red; becomes a defect at RC (F-01) | ui (PR #29 binds `AppEngine.engine`) |
| Property.INV12 ReapplyEqualsEngineFold | RED BY DESIGN, same cause | design-red, becomes a defect at RC | ui |
| Property.INV04 RequestBounds | counterexample seed 1: reconciliation reads at 2000, 6000, 14000, 37645, 53675 ms … (more than 4 in 60 s) | **contradiction** DEV-R8-11 (T-S31/T-S32 against INV-04; no clock); waiting for the owner | coordinator → owner |
| Live.INV22 ThreeSimultaneousCommits | BLOCKED locally (no CAUSA_WRITE_TOKEN). It runs in live.yml and passed in run 41344722282 | not a failure | persistence |

INV14 has passed since T22 bound `Projection.value`. Against T36, non-live failures fell from 4 to 3.

**Are the INV08 bindings honest?** Yes, with two caveats:
- They bind real implementations: `Kernel.domain`, `SaveProcess.forNewTab ()`, `StorageV1.codec`,
  `ErrorMatrix.classifier`, `Projection.value` and `InvariantFake`. `engine` stays `None` and fails loudly; nothing is
  stubbed to pass.
- Caveat 1: `forNewTab` uses `pendingCommandFiles` (`Session.fs:490-502`), a stand-in file set with one
  "CommandRecorded" event per command, instead of the real `fileSetOf` (`Session.fs:726`). The save-path invariants
  therefore check the protocol, not the bytes of kernel-decided events (F-07).
- Caveat 2: `kernel` is the frozen `Kernel.domain`, whose `Decide` is the degenerate Start/RecordObservation
  (DEV-R8-09). No invariant exercises `Kernel.decideOn`, the decision the app actually uses (F-07).

## 3. State machines

- SM-SAVE, SM-CONFLICT and SM-LOAD (`tests/Causa.Persistence.Tests/StateMachines/*.fs`), and SM-CONNECTION and
  SM-NETWORK (`tests/Causa.App.Tests/StateMachines/*.fs`):
  - every transition id in state-machine-contracts.json has a test of that name;
  - each `EveryIllegalPairFailsClosed` enumerates states × events minus the legal set, and asserts a typed fault naming
    the machine, state and input (SM-SAVE: 139 pairs, both online and offline).
- Every `step` ends in a fail-closed default (`Session.fs:324`, `Conflict.fs:99`).
- Weakness: SM-NETWORK is tested against a recording save machine, not SM-SAVE (F-05).

## 4. Ownership, process and CI

- **Ownership.** Every non-owner file change in a merged PR is attributable to a CR applied by integration:
  - CR-T15-01, CR-T20-01 and CR-T25-01 touched ci.yml and live.yml;
  - CR-T16, CR-T22-1, CR-T23 and CR-T21-01 merged owner branches;
  - T19 and T22 edited `INV08.fs` Bindings, which that domain-owned file explicitly invites by pull request.
  - WI-R8-T36FIX changed persistence files under the actor `claude-code/persistence` (.ros events), so the owner made
    the change.
  - No unexplained non-owner edit was found.
- **Skipped or weakened tests:** none. There is no `Skip=`, `test.skip`, `test.fixme` or `.only`. NOT_RUN parts are
  labelled in the tests themselves (for example `PERSIST-033.spec.js:3`, O3 NOT_RUN).
- **CI gates:** the gated non-live step is honest (it fails on a non-zero exit, on an empty run and on skips), but see
  F-02 and F-06.

## Findings

| ID | Severity | Where | Owner | Class |
|---|---|---|---|---|
| F-01 | **blocker (RC)** | `src/Causa.App/Program.fs:21, 96-98, 123-124`; `Engine.fs:78`; `INV08.fs:39`; `web/boundary.js`; `web/index.html` | ui (Engine) + integration (Program.fs, boundary.js, index.html) | missing integration (T36 F-03 still open on main) |
| F-02 | **major** | `.github/workflows/ci.yml:93` (also :70, `build-and-test.yml:46`, `candidate-acceptance.yml:76`) | integration | gate defect (evidence is lost) |
| F-03 | **major** | `src/Causa.App/Update.fs:44-46` | ui (coordinator if a contract change) | data loss on conflict export |
| F-04 | minor (latent) | `src/Causa.App/Update.fs:48-51` | ui | guard bypass (T-S04/T-S53) |
| F-05 | minor | `src/Causa.App/Update.fs:73`; `tests/Causa.App.Tests/StateMachines/SMNETWORK.fs:31-39, 63-76` | ui | weak test plus a silently swallowed fault |
| F-06 | minor | `.github/workflows/live.yml:48` | integration | gate gap (an allow-list) |
| F-07 | minor | `tests/Causa.Invariants/Model/INV08.fs:29-31`; `Session.fs:490-511` | domain / persistence | binding scope |

### F-01 (blocker for RC) The browser product path is not wired: no effects, no Engine, no connect

- **Code.**
  - `Outgoing = { View; Effects: unit list; … }` (`Program.fs:21`). `step` always returns `Effects = []` (:98), and
    `encode` always writes an empty `"effects"` array (:123-124).
  - `Incoming.EffectResult` and `CapabilityFact` leave the state unchanged (:96-97).
  - `type Engine = { Init; Update }` (`Engine.fs:78`) has no implementation in `src`.
  - Neither `Update.network` nor `Update.connection` (`Update.fs:57, 84`) is called from Program.fs.
  - `web/boundary.js` performs no HTTP, and `web/index.html` has no repository or credential form.
  - The only user commands are canned instances selected by event name (`View.fs:64-65`).
- **Failure scenario.** At the deployed SHA a user opens Causa, clicks Start investigation, and can never connect a
  repository, open a catalog case or save. Every SM-SAVE, SM-CONFLICT and SM-LOAD transition is unreachable from the
  page. T36 F-03 (T-C04 read with no Send, T-C12 empty export) is still open on main.
- **Fix.** Merge PR #29 (binds `AppEngine.engine`; INV07 and INV12 should then go green) and add an integration CR so
  that:
  - Program.fs routes Limen events through `Engine.Update` and encodes `HostEffect` (Http, Timer, LiveAnnounce,
    Download) as Limen effects;
  - EffectResult is decoded back into `Msg`;
  - index.html gains the connect form (credential in memory only, PF-015).
- **Proof that it is fixed.** Playwright at the deployed SHA drives connect, open, edit, save and conflict against
  causa-write-test.

### F-02 (major) The CI invariant ratchet publishes nothing

- **Code.** `ci.yml:93` runs `output=$(dotnet test …)` followed by `status=$?`. GitHub runs this as `bash -e -o
  pipefail`, so the failing command substitution ends the step before `echo` runs.
- **Repro.** `bash --noprofile --norc -e -o pipefail -c 'set -uo pipefail; o=$(echo out; false); s=$?; echo "after $s"'`
  prints nothing and exits 1. In run 41344722346 (1a40163), the "Invariant suite (ratchet)" step prints only
  `##[error]Process completed with exit code 1.`, with no test lines and no counts. `continue-on-error` keeps the job
  green.
- **Failure scenario.** A PR that raises the invariant failure count (DEV-R8-06 forbids that) merges green. Nobody can
  see the count in CI, and the "published counts" do not exist. The same pattern in `ci.yml:70` and
  `build-and-test.yml:46` still fails the gate, but it loses every diagnostic line on failure.
- **Fix.** `output=$(…) && status=0 || status=$?`, or `set +e` around the call. Compare the count with a committed
  baseline so the ratchet is mechanical. At T31, drop `continue-on-error`.

### F-03 (major) "Export unsaved changes" exports command ids only

- **Code.** `Update.hostEffect (ExportCommands commands)` writes `{"commands":["<guid>",…]}` (`Update.fs:44-46`).
- **Failure scenario.**
  1. A conflict occurs, and the user chooses Export (T-C12) and then Discard.
  2. The file holds GUIDs, which cannot rebuild a single change, so the user's work is gone.
- **Cause.** The frozen `CommandEnvelope` has no body (DEV-R8-09).
- **Fix.** The engine must export from its own store, the decided `EventEnvelope`s or the typed `Command`s it
  submitted, keyed by those ids. The coordinator must decide if a contract change is needed. Add a test: export → a
  fresh tab → import reproduces the same history.

### F-04 (minor, latent) The one-mutation-per-tab guard is hard-coded off

- **Code.** `Update.requestContext` sets `MutationInFlightElsewhere = false` (`Update.fs:51`). SM-SAVE decides T-S04
  against T-S53 from that flag (`Session.fs:212, 707`).
- **Failure scenario.** Once SaveRequested is wired (PR #29), saving investigation A while B's mutation is in flight
  sends a second createCommitOnBranch instead of holding the request (T-S53). Rehearsal 7 L2 regresses.
- **Fix.** Derive the flag from `model.Sessions`: true if any other session is Saving with an outstanding mutation.
  Check PR #29 for this.

### F-05 (minor) SM-NETWORK does not prove composition with SM-SAVE, and SM-SAVE faults are swallowed

- **Code.** `Update.network` folds `| Error _ -> current, effects` (`Update.fs:73`) with no fault surfaced. The T-W01
  test's recording machine refuses `Saved + WentOffline` (`SMNETWORK.fs:31-39`).
- **Effect on the test.** The assertion "Saved stays Saved" (:70) passes through the swallowed-error branch, not through
  T-S41.
- **Failure scenario.** If SM-SAVE refused any WentOffline or WentOnline pair (all 14 are legal in the contract), the
  network step would hide it, and the header and indicator would drift from the session.
- **Fix.** Use `SaveProcess.create` in T-W01/T-W02, and return or announce SM-SAVE faults.

### F-06 (minor) The live suites run from a hand-kept allow-list

- **Code.** `live.yml:48` lists classes by name, and `ci.yml:70` excludes every `.Live.` test.
- **Failure scenario.** A new `Causa.Acceptance.Live.*` class (for example PERSIST-044/046 for T31) runs nowhere and
  reports nothing.
- **Fix.** Filter with `FullyQualifiedName~.Live.`. Today all 45 Live acceptance tests and INV22 are listed.

### F-07 (minor) What the invariant bindings cover

- **Save machine.** The saveMachine binding writes stand-in files (`pendingCommandFiles`), not `fileSetOf codec opened
  decided` (`Session.fs:726`).
- **Kernel.** The kernel binding is the frozen `Kernel.domain`, not `decideOn`.
- **Effect.** No invariant would catch a defect in how real decided events are encoded for a save, or in the lifecycle
  decision.
- **Fix.** Once the engine is bound, add an INV-05/INV-11 case that saves through the engine's `fileSetOf`.

## P0 gap list for T31 (release candidate: full applicable matrix at the deployed SHA)

1. **The browser product path (F-01).** Steps 1-3 need integration CRs.
   1. Merge PR #29 (Engine bound).
   2. Program.fs encodes HostEffects and decodes EffectResult.
   3. boundary.js performs Http, Timer and Download through Limen; the index.html connect form.
   4. INV07 and INV12 green.
2. **Live persistence at the deployed SHA, through the page.** PAT-02..07 and PERSIST-005/006/007/021/041-043
   currently pass only as library tests (live.yml 45/45 at 1a40163). T31's PERSIST-040/044/046/048/050/052/053/054 need
   the page path from item 1 and their own Live tests, which must be added to live.yml (F-06).
3. **Fix the invariant gate (F-02).** Every invariant must pass or be formally decided by the owner. INV04 stays
   contradictory (DEV-R8-11) until the owner chooses a clock in SaveSetup or amends T-S31/T-S32. T31 cannot report
   INV-04/PERSIST-016 as verified.
4. **Conflict export and the engine duties (F-03, T36 F-03).**
   - T-C04/T-C13 send the read with the recorded RequestId.
   - T-C12 exports restorable content.
   - Engine-level tests for each.
5. **Pages behind the RC that are not merged.**
   - T26 Report/Folio is PR #25 (`Views/Report.fs` is a stub).
   - T27 has a branch and no PR.
   - T28 History, T29 Administration and Dashboard are 2-line stubs.
   - Each is a T31 dependency in execution-dag.json.
6. **Dialogs.** The Discard and Disconnect confirmations need `<dialog>` invokers declared (AGENT-RULES) and the Forma
   layout-tests run on them.
7. **Single-mutation guard (F-04)** derived from the model before SaveRequested ships.

## Obligations (T30)

- AR-008 (a fresh reviewer, code and requirements only): met. No author summary was used for any finding.
- DCC-002/DCC-003 (findings by class, with a reproduction): met. The blocker and the major findings have owners;
  fixes must be re-reviewed by a reviewer who did not see their rationale (R7L-13).
- `doc:docs/DESIGN-DECISION-BOUNDARIES.md`: D-006 is enforced by the fail-closed steps; D-009 holds; D-014 holds
  (Causa.Domain references no persistence project). D-012 and D-011 are not exercised by the app path yet.
