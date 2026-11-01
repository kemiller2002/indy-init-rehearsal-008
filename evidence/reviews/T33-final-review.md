# T33: fresh pre-verdict review (AR-008)

- Work item: WI-R8-T33 (reviewer lane), covers AR-008 (AR-008-O1 attempt to falsify high-risk claims; AR-008-O2 reviewers cannot self-ratify operator decisions). Gate G7; completion predicate: "no open critical defect class".
- Reviewer: claude-code/reviewer, instance reviewer-T33. No part in implementation; earlier reviews (T30, T35, T36) were not used as a starting point.
- Reviewed tree: `main` at `8c5ff7f165407b3a7b2eafbe342c693f52a075ff` (review started 22:44Z, 2026-11-01).
- Method: read the package (v4.1.1 execution-dag, v4 curation/tasks.json, state-machine-contracts.json, analysis/github-error-matrix.json, verification-execution-plan.json), `src/Causa.Persistence`, `src/Causa.App` (Program.fs, Update.fs, Connection.fs, Routes.fs), the SM-SAVE tests, a sample of acceptance tests, the workflows and the Actions runs on main (GitHub MCP `actions_list`). Two claims were reproduced with an F# script against the built `Causa.Persistence.dll` (script: `dotnet fsi` against `src/Causa.Persistence/bin/Debug/net10.0`; output quoted in F-01 and F-02). No code was changed.
- AR-008-O2: this review ratifies no operator decision. Every item marked "owner decision" below stays with the owner.

## Summary

| Id | Severity | Subject |
|---|---|---|
| T33-F-01 | P0-degraded | Large-file re-read not wired into save read-back or reconciliation; the bound test passes on helpers only |
| T33-F-08 | P0-degraded | WebKit INV-06 and INV-21 fail at 8c5ff7f; `Build and test` is red on main |
| T33-F-02 | P1 | Credential is plaintext in `HttpRequest.Headers`; `%A`/`ToString` print it |
| T33-F-03 | P1 | T31 matrix is stale (b0ba767) relative to the tree under judgment |
| T33-F-04 | P1 | T32P maps a guard-false SM-SAVE pair to T-S30 effects without a recorded deviation |
| T33-F-07 | P1 | T32W branch: bodies can be dropped while envelopes survive; silent fallback to an observation |
| T33-F-05 | note | Deployed-acceptance checks identity only; superseded runs were cancelled at 6938631 |
| T33-F-06 | note | Machine faults dropped silently at the app edge (fail-closed holds) |

No P0-blocking finding beyond the defect classes already recorded (unwritten bound tests, INV04/DEV-R8-11).

## Already recorded, not re-reported

These are referenced and are not counted as new findings: DEV-R8-01..11 (`evidence/DEVIATIONS.md`), in particular DEV-R8-09 / CR-T17-01 (frozen envelope carries no command body) and DEV-R8-11 (INV-04 versus SM-SAVE T-S31/T-S32; `INV04.RequestBounds` red); PERSIST-048 has no `beforeunload`; UI-004-O1 versus "discoverable, never hidden"; WI-R8-T32W (wired-session commands lose their body and are recorded as Observations: `Program.fs:351-357` builds a bare `CommandEnvelope`, `Update.decideAll` calls the frozen `Kernel.domain.Decide`, which decides RecordObservation for every command on an existing investigation). At 22:51Z T32W had a pushed branch (`origin/r8/t32w`, commit `10a1982`) but no PR. Its diff was reviewed as it stood; see T33-F-07.

## Findings

### T33-F-01 — P0-degraded: the large-file re-read is not wired into save read-back or reconciliation; the bound test passes without exercising it

- Files: `src/Causa.Persistence/Session.fs:362-385` (`checkFile`), `src/Causa.Persistence/Reconcile.fs:76-87` (`Answers.contents` accepts only `encoding: "base64"`), `src/Causa.Persistence/Session.fs:677-699` (`interpretContents`, `largeFileRetry`: referenced only from tests), `tests/Causa.Acceptance/Live/PERSIST007Tests.fs:91-104`.
- Reasoning: the matrix (GH-LARGE-FILE) says a contents read answered with `encoding: none` "is not an error for the user: the read is repeated once through the Git blobs API". The classifier accepts that answer as `GhLargeFile` (Ok). `checkFile` then decodes it with `Answers.contents`, which returns `None` for `encoding: none`, so the file is `Unreadable`. In read-back that is `ReadBackReadFailed` (T-S58, Saving to UnknownOutcome); in reconciliation the marker is found, the same `checkFile` is `Unreadable`, so `ReconcileReadFailed`, and after three reads T-S30 ("still unknown, choose Check again"). Check again repeats the same path. A save whose `events.ndjson` (or snapshot or manifest) is over 1 MB therefore never reaches Saved, although the commit landed. No blob request is ever sent: `grep -rn largeFileRetry src` finds only its definition.
- Reproduction (F# script against the built DLL):
  ```
  ErrorMatrix.classify (Read "rest:contents") <200, {"encoding":"none","content":"","sha":"abc123",...}>
    -> class=GhLargeFile ok=true
  Answers.contents <same body> -> None
  ```
- Test honesty: `PERSIST007Tests.ReadBackOfFileOver1MB` (plan-bound to PERSIST-007-O1) checks `GitHubProvider.interpretContents` and `largeFileRetry` as standalone helpers and a live blob read. It never drives the save machine through a read-back of a large file, so it passes while the save path does not do what it claims.
- Affected: PERSIST-007-O1 (confirmed only after a re-read at the commit returns the written bytes), GH-LARGE-FILE bound `largeFileRereads: 1`, PERSIST-015 (reconciliation decides by bytes at the marker commit). PERSIST-007-O1 is not-run in T31 for another reason (its deterministic test was a stub at b0ba767), so no "verified" count is false today, but the live half's "pass" must not be read as evidence for this behaviour.
- Severity rationale: no data loss and no duplicate write (the commit is on GitHub; the session holds the commands), so not P0-blocking for the common case. Any investigation above 1 MB per file cannot be confirmed as saved.
- Fix owner: persistence (wire `interpretContents`/`largeFileRetry` into `checkFile` with one blob re-read; add a deterministic SM-level test).

### T33-F-02 — P1: the credential is plaintext inside every request value; `%A` and `ToString()` of a request print it

- Files: `src/Causa.Persistence/Wire.fs:14-19` (`HttpRequest.Headers: (string * string) list`), `src/Causa.Persistence/Wire.fs:33-44` (comment: "The credential is opaque everywhere ... it prints as `<credential>`"), `src/Causa.Persistence/GitHubRequests.fs:13-29` (every request carries `Credential.authorization` in `Headers`).
- Reproduction (same script):
  ```
  let req = GitHubRequests.branchHead (RequestId 2L) { ...; Credential = Credential.create "ghp_REPRO_SECRET_VALUE" }
  (sprintf "%A" req).Contains "ghp_REPRO_SECRET_VALUE"  -> true
  (string req).Contains "ghp_REPRO_SECRET_VALUE"        -> true
  ```
- Reasoning: `Credential.T` is opaque, but `authorization` turns it into `"Bearer <token>"` at request construction, and requests are pure data that flow through `SaveEffect.Send`, `Interpretation.NextRequest` and `HostEffect.Http`. Any structural print of those values (xUnit `Assert.Equal` failure text on effect lists, `failwithf "%A"`, a future diagnostic) writes the token. In live CI the token would land in TRX files uploaded as the `live-test-results` artifact (`live.yml:62-68`). Actions masks registered secrets in logs, not in artifact files. The INV22 guard (`tests/Causa.Invariants/Live/INV22.fs:36-49`) checks only the recordings JSON. I found no current print of a request in the live tests (their `%A` calls print states, faults and outcomes), so there is no observed leak, and that is why this is P1. The "opaque everywhere" claim in Wire.fs is false as written.
- Affected: INV-21, PF-015, AR-014-O2 ("no secret value may reach an artifact"), EXT-DATA-005/007 security posture.
- Suggested direction (persistence/integration, possibly a frozen-interface question for the coordinator): keep the credential typed in the request (add the header at the host edge only) or give `HttpRequest` a redacting `ToString`/`StructuredFormatDisplay`. Add a check that no live-suite TRX contains a secret value.

### T33-F-03 — P1: the T31 matrix is stale relative to the reviewed tree; its "verified" claims are supported only at b0ba767

- Files: `evidence/RC/T31-matrix.json` (`candidate` b0ba767), `evidence/RC/T31-RC-REPORT.md`.
- Reasoning: since b0ba767, main has merged behaviour changes and many bound tests: WI-R8-EXPVER (`d5aab7e`, exported commands carry their decided version, so T31 defect F-03 is fixed, not open), WI-R8-T32P (`f6dc84b`, reconciliation decision rewritten, SM-SAVE T-S29 behaviour changed), ITESTS/ITESTS2/PTESTS/DTESTS/UTESTS (new acceptance and browser tests, INV-06/INV-10 identities, WebKit runs invariants). Spot check of 8 random verified rows (UI-018-O2, PERSIST-021-O3, UI-019-O3, PERSIST-030-O3, UI-031-O5, VER-008-O1, PERSIST-032-O3, UI-014-O2): each bound identity exists, matches the plan identity and has real assertions (for example `UI-018.spec.js:23-48`, `PERSIST021Tests.fs:44-56`). I found no skipped tests (`Skip=`, `.skip(`, `fixme`, `.only(`: none) and live tests fail rather than skip without a secret (`LiveGitHub.requireSecret`). 45 of 71 `tests/Causa.Acceptance/{Deterministic,Live}/*.fs` files are still 2-line bootstrap stubs at 8c5ff7f.
- Conclusion: the 113 verified at b0ba767 are supported as a statement about b0ba767 (method sound: an obligation is verified only when every bound identity passed). They cannot be carried to the final SHA. T34 must regenerate the matrix at the SHA it judges.
- Affected: R16/R14 acceptance ledger, VER-007, AR-008-O1.

### T33-F-04 — P1: T32P maps a guard-false SM-SAVE pair to another transition's effects without a recorded deviation

- File: `src/Causa.Persistence/Session.fs:305-313`; contract `state-machine-contracts.json` SM-SAVE (UnknownOutcome, ReconcileReadFailed): one transition T-S29 with guard "fewer than three automatic reads made"; `invalidTransition.rule`: fail closed, state unchanged, no effect.
- Reasoning: with three reads made, the contract offers no transition for (UnknownOutcome, ReconcileReadFailed). The session's own catch-all says that a pair whose guard does not hold fails closed (`Session.fs:322`), but this pair now runs T-S30's effects (it sets `ReconcileReads = 3`, clears Outstanding, announces "choose Check again"). The outcome is what the user needs, and it fixes a real stuck-session defect. The literal contract route is for the interpreter to emit `ReconcileReadsSpent` when no automatic read is left. `tests/.../SMSAVE.fs` T-S29 now asserts this mapping, so the plan identity `SMSAVE.T-S29` tests behaviour outside T-S29. Not in DEVIATIONS.md.
- Affected: PERSIST-016, PERSIST-023/051 (fail closed), state-machine legality. Action: record it as a deviation (coordinator) or move the mapping to the interpreter (persistence). Owner decision whether the table should say so.

### T33-F-05 — note: the deployed SHA briefly had no completed test run; deployed acceptance checks identity only

- Evidence (Actions, main): deploy run 41358519988 at `6938631` succeeded, including `deployed-acceptance` ("The deployed site serves this exact SHA and its engine", job 125004056127). The served `deployed-sha.txt` read at 22:46Z was `693863196eb05f73e6d9a2b2e1bae9cfd4aed7c7`. The CI, build-and-test and live runs at `6938631` were **cancelled** (41358519940, 41358519950, 41358519960), superseded by `8c5ff7f`. At `8c5ff7f` (rechecked 22:56Z): CI 41358592491 success, live 41358592497 success, deploy 41358592545 success, and build-and-test 41358592530 **failure** (WebKit, see T33-F-08). At 22:52Z the served `deployed-sha.txt` was `8c5ff7f165407b3a7b2eafbe342c693f52a075ff`, so the deployed SHA equals main, where CI, live and deploy are green and build-and-test is red.
- D-T31-01 (the pipefail in deployed-acceptance) is confirmed fixed: deployed-acceptance is green at 6938631.
- The deployed check (`.github/workflows/deploy.yml:74-101`) proves the SHA and that the engine assets are served. It runs no behaviour. SU-BROWSER-LIVE (`tests/browser/live`) still does not exist, so no deployed-live obligation can be verified. This is already in T31 (defect 4); restated only to qualify `evidence/DEPLOYMENT.md` / `LIVE-PROVIDER.md`.
- Action for T34: judge only a SHA where deploy (with deployed-acceptance), CI, build-and-test and live have all completed successfully.
- Also note: the invariant step is `continue-on-error` (DEV-R8-06), so its step conclusion "success" in run 41358592491 does not show whether INV04 still fails. Read the TRX.

### T33-F-06 — note: machine faults are dropped silently at the app edge

- File: `src/Causa.App/Update.fs:319, 336, 377, 395, 423, 529` (`| Error _ -> model, []`), `Update.fs:110` (faults collected).
- Reasoning: fail-closed is honoured (model unchanged, no effect), which satisfies the invalid-transition rule. The typed fault, which names state and event, is not announced or logged, so an illegal event from the UI (for example `save.Save` while Saved) does nothing visibly. This is acceptable under the contract. Listed so that T34 does not count "fault surfaced to the user" as verified.
- Affected: PERSIST-023, PERSIST-051 (verified at the machine level by `EveryIllegalPairFailsClosed` in all five machines: SMSAVE 139 pairs, SMCONFLICT 81, SMCONNECTION 21, SMLOAD 26, SMNETWORK 2).

### T33-F-07 — P1 (in-flight fix WI-R8-T32W, branch `r8/t32w` at `10a1982`, no PR yet): command bodies are tab memory beside the session and can be lost while the session's commands survive

- Files on the branch: `src/Causa.App/Program.fs` (new `Tab.Commands: Map<Guid, Command>`, `configOf`, and the `connect.disconnect` handler setting `Commands = Map.empty`), `src/Causa.App/Update.fs` (`Config.CommandBody`, `decideAllWith`: a command whose body is unknown falls back to the bare frozen `Decide`, which is RecordObservation).
- The approach is sound for the main path. The body is kept by command id, every engine decision path (`decidedOn`, `fileSetFor`, `advanceBase`, conflict reapply) switches to `decideKnown`, and the frozen types are unchanged (DEV-R8-09 respected).
- Defect: the fallback is silent. Any route that drops a body while its envelope stays in `Session.Unsaved` brings back exactly the T32W bug for that command (written to GitHub as an observation). One such route exists on the branch: `connect.disconnect` clears `Commands` unconditionally *before* the engine decides the input. `ConnectionMachine.step` accepts DisconnectConfirmed only from Connected and CredentialRefused (`src/Causa.App/Connection.fs:64-66`), and only then drops the sessions (`DropCredentialAndTabState`). From Connecting or Reauthenticating the input fails closed: sessions and unsaved commands are kept, the bodies are gone, and the next save writes them as observations. A missing body should refuse the command (or block the save), not decide it as an observation. `decideAll` (no bodies) is still public but has no caller in `src/` on the branch. Any future caller would be a second silent route.
- Also (notes): `Tab.Commands` is never pruned after confirmation (it grows for the life of the tab). Export (`Update.exportJson`) still writes envelopes without bodies, so an exported unsaved command cannot be reapplied with its meaning. That is the recorded DEV-R8-09 class, not new, but T32W does not close it.
- Affected: the T32W target (UI commands decided as typed, the domain lifecycle table), PERSIST-007-O3, PERSIST-011/012 (reapply keeps meaning). Fix owner: ui (clear bodies only when the engine actually drops the sessions, or derive the clear from the new model; treat an unknown body as a refusal).

### T33-F-08 — P0-degraded: at 8c5ff7f the WebKit job fails INV-06 and INV-21 (the storage and credential invariants)

- Evidence: build-and-test run https://github.com/kemiller2002/indy-init-rehearsal-008/actions/runs/41358592530, job `webkit` (125003969040) **failure**, 84 passed and 2 failed: `tests/browser/invariants/INV-06.spec.js:5` "every store empty of investigation content" and `tests/browser/invariants/INV-21.spec.js:50` "no credential in storage, URL or artifact". Both fail with `page.evaluate: UnknownError: The operation failed for an unknown transient reason (e.g. out of memory)`. Chromium (job 125003969295) passed. WebKit started running `tests/browser/invariants` only after T31 (WI-R8-ITESTS, `4f4d79f`), so this is new since the T31 matrix.
- Reasoning: the failing step is the storage probe itself (`INV-21.spec.js:29-43`, `INV-06.spec.js:28-40`: `indexedDB.databases()`, `caches.keys()`, OPFS and similar), not an assertion that found a credential or content. So this is most likely a harness or WebKit-on-Linux limitation, not evidence of a leak. Either way, the two P0 security invariants are **not verified on WebKit** at the current main, and the CI workflow `Build and test` is red on main.
- Affected: INV-06, INV-21, PF-015, AR-014-O2, the SU-BROWSER-WEBKIT obligations bound to these titles. Owner: integration/ui (make the probe total: catch per-store errors and report "probe unsupported" as a distinct, failing-or-recorded result rather than a crash; do not weaken the assertion).
- Not counted in the T31 matrix (it predates the change). T34 must treat these as failed, not not-run, unless a re-run at the judged SHA passes.

## Checks that held (falsification attempted, nothing found)

- Credential storage and URL: no `localStorage`/`sessionStorage`/IndexedDB use in `src/` or `web/`. The token draft lives only in the tab model and is cleared on Connect (`Program.fs:309-318`). The token inputs are mounted only while needed (`web/index.html:124,128`, `type=password`, `autocomplete=off`). `Routes.fs:63-64` strips credential-like query keys. No token-shaped string is committed except clearly fake test tokens. Secrets appear only in `live.yml`, which runs on `push` to main and `workflow_dispatch`, never `pull_request` (tokens CI-only, not exposed to forks).
- Connect: write access only from the repository `permissions` object (`ErrorMatrix.successBody` ConnectRepository), and a token alone never connects.
- Error matrix: `ErrorMatrix.classify` follows the stated precedence (GH-AUTH 401; primary with remaining 0; secondary with retry-after or message; stale head; large file; permission; validation; not found; then the rest). It maps a 5xx or an unrecognised GraphQL error on a mutation to unknown outcome (reconciled, `meaning`/`meaningOf`, including a bare 404 on the GraphQL endpoint). Bounds 4 mutations / 3 rebases / 3 reconciliation reads (2 s, 4 s, 8 s) match `Session.fs:30-35` and the matrix `bounds`.
- Reconciliation (post-T32P): deciding "written" by the marker on the first commit after the guarded base head is sound, because createCommitOnBranch with `expectedHeadOid` can only land directly on that head. `sendMutation` moves `BaseHead` to the rebase head, so the marker check stays correct after a rebase. An unknown or capped file list counts as changed (no overwrite on a guess).
- Exported command versions (T31 F-03): fixed by `Update.decidedOn` (`Update.fs:221-229`, applied at `Update.fs:519`).

## Recommendation for the verdict

**Not releasable; do not accept.** This review agrees with T31's verdict and adds nothing that would overturn it. The release acceptance predicate ("no open critical defect class") is not met:
1. Unwritten bound tests remain a defect class (45 of 71 acceptance .NET files are stubs; SU-BROWSER-LIVE, SU-VERIFIER and the process records are absent). Most P0 obligations cannot be verified.
2. INV04 RequestBounds is red pending the owner decision (DEV-R8-11).
3. T33-F-08: `Build and test` is red on main (WebKit INV-06 and INV-21), so two P0 security invariants are unverified on WebKit.
4. T33-F-01 adds a P0-degraded save defect (no confirmation above 1 MB per file) and shows that a passing bound test does not exercise the behaviour.
5. The T31 matrix describes b0ba767, not the tree under judgment (T33-F-03).
6. The in-flight T32W fix leaves a silent route back to the defect it fixes (T33-F-07). Merge it only with that route closed, or with the route recorded.

If T34 computes the verdict, it should (a) regenerate the matrix at a SHA whose CI, build-and-test, live and deploy (with deployed-acceptance) runs all completed successfully; (b) not count PERSIST-007-O1 as verified on the strength of `ReadBackOfFileOver1MB`; and (c) leave the AR-008-O2, AR-024 and DEV-R8-04/05/09/10/11 items with the owner (this reviewer ratifies none of them).

## P0 claims I could not confirm

- Live behaviour at `8c5ff7f`: live.yml succeeded, but its per-test TRX was not read, so I cannot say which live identities ran (stubs still compile to nothing). WebKit is red (F-08).
- Whether `INV04.RequestBounds` (or any other invariant) fails at 8c5ff7f: the step is continue-on-error, and the TRX was not read.
- Opening an investigation whose files exceed GraphQL `Blob.text` limits (`Reconcile.fs:145-158` treats `isTruncated` as an unreadable file set). Likely the same class as F-01 on the load path. Not reproduced.
- Browser-side handling of `HostEffect.Http` inside Limen (an external package): whether any Limen diagnostic can print request headers.
- WI-R8-T32W: the branch diff at `10a1982` was read (F-07), but its tests were not run and no PR existed. A later push to that branch is not covered.
- Any P0 obligation whose bound tests are stubs. Their behaviour was not reviewed beyond the paths above.
