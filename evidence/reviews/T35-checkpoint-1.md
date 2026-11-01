# T35 — Independent review checkpoint 1: interfaces and invariants

- Reviewer: claude-code/reviewer (WI-R8-T35), independent of the T12, T14 and T18 authors; formed from code and the package, not from PR descriptions (AR-008).
- Covers: DCC-002 (O1 bounded early review of interfaces and invariants, O2 not waiting for browser/deployment acceptance, O3 findings actionable, reproducible, assigned to a requirement/invariant).
- Reviewed at: `main` a15bcf8d6a32 (T14, CR-2, T18 merged); PR #6 `origin/r8/t12` 3a8802e69355 (base a4b5c49, does not yet contain T18).
- Time: started 18:07Z (T+36), recorded ~18:25Z (T+54), inside the DCC-002 target window (T+90..120) and before any release candidate or browser/deployment acceptance (DCC-002-O2).

## Verdict: PASS WITH FINDINGS

No blocker. Interfaces A–E are intact and the manifest is correct. PR #6's invariant tests are honest (RED BY DESIGN tests fail, none skip, the 8 passes test production code) and can merge; the findings below are weakenings to fix before T30, each with an owner.

## Commands run (evidence)

| Command | Tree | Result |
|---|---|---|
| `dotnet build Causa.slnx` | main a15bcf8 | 0 warnings, 0 errors |
| `dotnet test tests/Causa.Contract.Tests` | main | Passed 7/7 |
| `dotnet test tests/Causa.Invariants` | main | 1 test (Live.INV22.ThreeSimultaneousCommits): Failed, no CAUSA_WRITE_TOKEN locally. Honest: it fails without the secret and does not skip or pass |
| `dotnet test tests/Causa.Persistence.Tests` | main | no tests discovered (none owned yet) |
| `dotnet build Causa.slnx` | r8/t12 3a8802e | 0 warnings, 0 errors |
| `dotnet test tests/Causa.Invariants` | r8/t12 | 35 cases: 8 passed, 27 failed, 0 skipped; every failure is a `RED BY DESIGN` message |
| scratch merge of r8/t12 into main + bind `Bindings.classifier = Some(ErrorMatrix.classifier (Mutation oid))`, run `Negative.INV22` | throwaway worktree, not pushed | source merges cleanly (only `.ros/` conflicts); INV22.RecordedGitHubAnswerForms **passes** against T18's classifier |
| `dotnet fsi` probe of `ErrorMatrix.classify` edge forms | main build output | results quoted in R-03..R-06 |
| SHA-256 and verbatim-containment script over `evidence/interface-manifest.json` | main | 9/9 hashes match; 9/9 producer files contain the spec text byte-for-byte |

## 1. Interfaces (boundaries A–E)

- `00-DOMAIN-STUBS` → `src/Causa.Domain/Identity.fs`, `01-IF41-DOMAIN` → `Kernel.fs`, `02` → `Storage.fs`, `03` → `Wire.fs`, `04` → `Classify.fs`, `05` → `SaveMachine.fs`, `06` → `Engine.fs`, `07` → `Projections.fs`, `08` → `Reporting.fs`: each `specSha256` in `evidence/interface-manifest.json` equals sha256 of `requirements-compiled/v4.1.1/contracts/*.fs`, and each producer file contains the spec text verbatim (`producerContainsSpecVerbatim: true` is correct).
- Shadowing check: I searched every frozen type name for a second definition under `src/`. The only duplicate is `Route`, defined in `src/engine/Routes.fs:18` under namespace `IndyInitRootCauseInvestigator.Engine`. That is the separate bootstrap project App.Engine, a different namespace with no reference from Causa.App, so nothing is shadowed.
- `Kernel.fs:22-40` (T14 `ReplayOrder`): a `[<RequireQualifiedAccess>]` module that adds a function returning the frozen `ReplayError`. It changes no frozen type. Accepted.
- `Classify.fs:39-307` (T18 `ErrorMatrix`): a nested module. Its types (`MatrixClass`, `AnswerContext`, `Classified`, `GraphQLError`) are new names and redefine no frozen case. `classifier : AnswerContext -> Classifier` returns the frozen type. Accepted, but see R-01: the frozen `Classifier` takes no context, and the matrix's 404 needs one.

## 2. Invariants (PR #6, r8/t12)

- Identities: the 22 `dotnet:Causa.Invariants.*` test ids in `v4/invariants.json` are exactly the 22 in `v4.1.1/verification-execution-plan.json`. PR #6 implements 21 of them. The 22nd, `Live.INV22.ThreeSimultaneousCommits`, is persistence-owned and was delivered by T18 on main. Namespaces, module names and test names match the ids, and every file is domain-owned per agent-file-ownership.json except Live/INV22 (persistence).
- Honest RED: every unbound implementation goes through `Require.bound` (`Model/INV08.fs:42-57`), which `failwithf`s with `RED BY DESIGN`. `INV24` fails explicitly while `web/causa.css` is missing. No test is skipped or ignored. A theory with mixed cases (INV11, INV21) fails at identity level, so no identity reports PASS.
- Meaningful passes:
  - `Mutation.INV01.ReadBackSkipped` runs the production `Confirmation.ofReadBack` over 300 generated cases, and the injected read-back-skipped mutant is caught. Real kill.
  - `Negative.INV11` `replay-gap/-duplicate/-reorder/-genesis/-empty` run the production `ReplayOrder.contiguousFrom`. Real refusals (see R-08 on the mutant line).
  - `Negative.INV21` `formatting` and `authorization-header` run the production `Credential` (`Wire.fs:36`) through ToString, %A, %O, interpolation and JSON, and check the exact `Authorization: Bearer` header.
- No invariant is missing. INV-06/10/13/15–20/23 and the INV-21/24/25 browser rows are bound to Playwright, process or verifier tests that this project does not own. Weakenings are in R-02, R-07, R-09 and R-10.

## 3. T18 classifier and recordings (merged)

- The precedence order at `Classify.fs:261-304` is AUTH, RATE-PRIMARY, RATE-SECONDARY, STALE-HEAD, LARGE-FILE, PERMISSION, (404 by context), VALIDATION, NOT-FOUND, then the rest. This is the matrix's `precedence` order. Stale head is decided from the error content, never from the 200 status. 404 is split by `AnswerContext` (Connect → PERMISSION, Compare → HISTORY-REWRITTEN, Read → NOT-FOUND).
- `maxAttempts` (`Classify.fs:86-103`) matches every row's `maxAttempts` (RATE-PRIMARY 3, STALE-HEAD 3, NETWORK-AFTER 3, UNKNOWN 3, MALFORMED 3, TRUNCATED 3, LARGE-FILE 1, the rest 0). No code consumes it yet (R-11).
- Recordings (`tests/Causa.TestSupport/Recordings.fs`): all 15 forms are `DocumentedForm`, and their source says "message text illustrative, not captured in Rehearsal 8". `captured = []` stays empty until a CI probe artifact is committed. Nothing is presented as a live capture. Labelling is honest.

## Findings

| ID | Class | Severity | Where | Owner | Assigned to |
|---|---|---|---|---|---|
| R-01 | interface gap | **major** | `tests/Causa.Invariants/Model/INV08.fs:34` (`classifier: Classifier option`) vs `src/Causa.Persistence/Classify.fs:306-307` | domain (test) + persistence | INV-22, PERSIST-008-O2 |
| R-02 | weakened invariant test | **major** | `tests/Causa.Invariants/Negative/INV22.fs:24-65` | domain | INV-22, PERSIST-008-O2 |
| R-03 | classifier precedence | **major** | `src/Causa.Persistence/Classify.fs:176-178, 274-275` | persistence | INV-22, PERSIST-008, GH-PERMISSION / GH-RATE-* |
| R-04 | classifier totality | minor | `src/Causa.Persistence/Classify.fs:215` | persistence | IF41-ERRORS ("every provider answer maps to exactly one case") |
| R-05 | matrix fallback rule | minor | `src/Causa.Persistence/Classify.fs:281, 303-304` | persistence | github-error-matrix `description`, GH-UNKNOWN-OUTCOME |
| R-06 | invented value | minor | `src/Causa.Persistence/Classify.fs:253`, and `223-226` (missing `content` accepted) | persistence | GH-STALE-HEAD, GH-MALFORMED |
| R-07 | weakened invariant test | **major** | `tests/Causa.Invariants/Negative/INV11.fs:100-113, 145-150, 136-141` | domain | INV-11, PERSIST-005-O2 |
| R-08 | vacuous assertion | minor | `tests/Causa.Invariants/Negative/INV11.fs:33-41` | domain | INV-11 |
| R-09 | weakened invariant test | **major** | `tests/Causa.Invariants/Property/INV04.fs:17-27, 44-58` | domain | INV-04, PERSIST-016-O5 |
| R-10 | weakened invariant test | minor | `tests/Causa.Invariants/Property/INV12.fs:59-62` | domain | INV-12, PERSIST-012-O3 |
| R-11 | unenforced bound | minor | `src/Causa.Persistence/Classify.fs:86` (`maxAttempts` has no consumer) | persistence (T19/T21) | INV-04, PERSIST-010/016 |

### R-01 (major) The frozen `Classifier` has no context, but the matrix needs one; INV-22's binding can only cover one context
`Bindings.classifier` has type `Classifier` (`HttpOutcome -> Result<…>`). T18's classifier is `AnswerContext -> Classifier`, because the matrix makes 404 mean PERMISSION (Connect), HISTORY-REWRITTEN (compare) or NOT-FOUND (read). Any binding fixes one context. Failure scenario: the owner binds `ErrorMatrix.classifier (Mutation oid)` (I did this in a scratch merge and INV22 passed) or `(Read _)`. INV-22 then turns green while no 404 form, and no Connect or compare form, is classified under the invariant. The tests that the context exists to separate never run. Fix: change the test-side binding to `(ErrorMatrix.AnswerContext -> Classifier) option`, or to a list of (context, form, expected class). This is test-side and is not a frozen-interface change. No CR needed.

### R-02 (major) INV22.RecordedGitHubAnswerForms does not cover "every recorded GitHub answer form"
The test's own hard-coded list (`INV22.fs:24-65`) does not use `Recordings.all` (T18) and has no 404 forms, no GH-LARGE-FILE (encoding none / 403 too_large), no GH-HISTORY-REWRITTEN (compare diverged/behind/404), no GH-TRUNCATED-LIST, and no KD-020 form of a **403 secondary-limit message without `retry-after`** (its only secondary 403 also carries `retry-after`). Failure scenario: a classifier that sends 403 "You have exceeded a secondary rate limit" without retry-after to GH-PERMISSION ("this credential cannot write") passes INV-22. Fix: drive the test from `Recordings.all` plus the missing forms, each with its context (R-01). The test must stay correct once `captured` fills with CapturedLive entries.

### R-03 (major) A 403 whose message names a primary rate limit, with no `x-ratelimit-remaining: 0`, is classified GH-PERMISSION
Repro (`dotnet fsi` against main): `classify (Read "f") (Answered(id, 403, [], {"message":"API rate limit exceeded for user ID 1."}))` → `GH-PERMISSION (PermissionDenied "API rate limit exceeded…")`. The same happens with `x-ratelimit-remaining: 5`. The GH-PERMISSION row requires the message to name "neither a rate limit nor abuse", but `mentionsSecondary` (`Classify.fs:176-178`) only looks for "secondary rate limit", "too quickly" and "abuse". So the plain 403 status decides. Failure scenario: a proxy or GitHub drops the rate headers. The user is told "This credential cannot write to the repository" with Retry not offered, when they only need to wait. This is the classification-by-status-alone that the matrix forbids. Fix: treat any message naming a rate limit as rate-limit evidence. That means GH-RATE-PRIMARY when remaining is 0 or absent with a reset header, otherwise GH-RATE-SECONDARY, or GH-UNKNOWN-OUTCOME if the coordinator prefers. Either way never GH-PERMISSION. Add the form to R-02's list.

### R-04 (minor) The classifier throws on a 2xx compare whose `total_commits` is not an Int32
Repro: `classify (Compare oid) (Answered(id, 200, [], {"status":"ahead","total_commits":"3","commits":[]}))` throws `InvalidOperationException` from `GetInt32()` at `Classify.fs:215`. With `99999999999` it throws `FormatException`. IF41-ERRORS says every answer maps to exactly one case, and the matrix says a 2xx missing or misshaping a required field is GH-MALFORMED. Failure scenario: an unexpected compare body crashes the reconciliation update instead of counting as a failed read. Fix: use `TryGetInt32` and map a failure to `GhMalformed`. A `null` raw body also escapes `tryParse` (`:? JsonException` only).

### R-05 (minor) Answers outside the matrix are not split into "unknown for a mutation, failed read otherwise"
- 404 on the GraphQL mutation endpoint → `GH-NOT-FOUND (NotFound "graphql endpoint")` (`Classify.fs:281`). The matrix's NOT-FOUND row covers a REST read or a GraphQL NOT_FOUND error. A form outside the matrix on a mutation is GH-UNKNOWN-OUTCOME.
- 5xx on a read → `GH-UNKNOWN-OUTCOME` (`Classify.fs:303-304`). That row is "HTTP 5xx on the mutation". For a read it is a failed read.

Failure scenario: a consumer that keys SM-SAVE on `MatrixClass` puts a failed reconciliation read into the Unknown path, or treats an unknown mutation outcome as a definite refusal. Fix: branch on `AnswerContext` for the fallback.

### R-06 (minor) A fabricated empty commit oid, and an accepted read that has no content
- `Classify.fs:253`: STALE_DATA in a non-mutation context yields `StaleHead(CommitOid "", None)`. That is an invented expected head, not one the tab read. Prefer GH-UNKNOWN-OUTCOME/Unrecognised there.
- `Classify.fs:223-226`: a contents read with no `content` (and encoding not "none") is ACCEPTED. The matrix lists "lacks … the content of a file" under GH-MALFORMED. This is acceptable only if the Reader decodes it as Malformed, and that should be tested.

### R-07 (major) INV-11 mutants do not show that *only* the named check rejects the forged set
INV-11 requires "a forged file set that only that check rejects … removing the check makes the test fail" (the defect that survived Rehearsal 7). The storage cases accept a broader refusal:
- `storage-snapshot-equals-replay` adds a property `forgedState` to the snapshot (`INV11.fs:100-113`) and accepts `SchemaInvalid` (`:149`). With a strict causa-storage schema (no additional properties) the schema check rejects the forgery. The snapshot-equals-replay check can then be deleted and the test still passes, a surviving mutant.
- `storage-required-field` accepts `MissingRequiredField` or `SchemaInvalid` (`:136-141`), so deleting the required-field check also survives.

Failure scenario: Rehearsal 7's surviving-mutant class repeats once the codec is bound. Fix: forge only the snapshot values (a schema-valid title change with a recomputed `snapshotSha256`), expect exactly the named failure, and add a mutation step per check, as `Mutation.INV01` does.

### R-08 (minor) The replay "mutant" assertion is tautological
`INV11.fs:41` asserts `Result.isOk (replayCheckRemoved genesis forged)`, and `replayCheckRemoved` is `fun _ h -> Ok h`. It can never fail, so it proves nothing about the test. The real protection is the `isError` assertion at `:40`, which is sound. The cases also do not check which `ReplayError` is returned: `replay-empty` should be `EmptyHistory`, the others `NonContiguousVersion(expected, found)` at the first break. Fix: assert the exact error and drop or rework the tautology.

### R-09 (major) INV-04's 60-second total-network-failure bound is not tested, and a request can be split by Offline
- The invariant says "under total network failure at most four mutations and four reconciliation reads in 60 seconds". `RequestBounds` has no virtual-clock window across save requests, and the plan's `checks` says "with a virtual clock".
- `saveRequests` (`INV04.fs:17-27`) starts a new request whenever Saving is entered from any state other than Saving or UnknownOutcome. A path Saving → Offline → (WentOnline) Saving is therefore counted as two requests. A machine that resets `Mutations` on reconnect could send 4 + 4 mutations under one SaveRequested and pass both the per-request and the per-step (`after.Mutations`, self-reported) checks.

Fix: count mutations from the effects since the user's SaveRequested, and add a windowed count over the virtual clock.

### R-10 (minor) INV-12: the Unsaved branch does not require that nothing was refused
`INV12.fs:59-62`: if reapply ends in `Unsaved`, the test checks `session.Unsaved = accepted` but not `refused = []`. Failure scenario: an engine that silently drops refused commands and returns to Unsaved passes, because they leave with no reason shown, against "refused commands carry the engine's reason". Fix: add `Check.expect (List.isEmpty refused)` in that branch.

### R-11 (minor) Retry bounds are declared but not enforced
`ErrorMatrix.maxAttempts` matches the matrix, but no code in SaveMachine, Reconcile or Session uses it yet. This is expected at T18. It is recorded so that T19/T21 bind it and INV-04 (once bound) proves it.

## Notes (not findings)
- PR #6 is based on a4b5c49 and predates T18. All `Bindings` are still `None`, including `classifier`, which T18 has since provided. After merge the persistence owner should bind it (with R-01's context fix). The bindings file is domain-owned, so persistence and ui bind by PR to domain, as the file says.
- INV-24 is RED only because `web/causa.css` (integration) is missing.
- Live.INV22 (`Live/INV22.fs:166`) tolerates GhRateSecondary as a live-only class, and `:170` accepts a lost answer as Accepted. Both are acceptable for a live race; the deterministic forms belong in R-02.

## DCC-002 status
- O1: interfaces, architectural boundaries and critical invariants were inspected before any release candidate, at T+36..54, ahead of the T+90..120 target. **Met.**
- O2: the review did not wait for browser or deployment acceptance. **Met.**
- O3: every finding above has a defect class, a file:line, a reproduction or concrete failure scenario, an owner, and an invariant or requirement. **Met.**
- T35 completion item 2 (critical findings owned and fixed or decided before the next checkpoint, T36): open. There are no blockers. The majors R-01/R-02/R-07/R-09 (domain) and R-03 (persistence) need an owner decision before T36.
