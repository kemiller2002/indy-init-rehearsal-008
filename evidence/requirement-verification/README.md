# Requirement verification of the investigation workflow

This is new evidence. It does not change, and is not part of, the
completed Rehearsal 8 results in `evidence/` (FINAL-REPORT.md and the rest).
It was produced by a local run of the pipeline that
`.github/workflows/requirement-verification.yml` runs in CI, at the commit
recorded in each file. The CI run of each later commit publishes the same
files as the artifact `requirement-verification-<sha>`; that artifact, not
this copy, is the evidence for that commit.

| File | What it is |
|---|---|
| `verification-manifest.json` | `praxis.verification-manifest/1`, compiled from `CAT-CAUSA-INVESTIGATION`, which is proposed, at kemiller2002/Indy-init `d6f5cb5`, with `requirements/requirement-implementation-map.json`. `contentHash` `dd9a2a75…681c0c` |
| `observations/` | `dokimos.requirement-observations/1`: the facts the probes recorded against the deployed page, one file per test |
| `playwright.json` | The Playwright results that show the probes' tests executed |
| `requirement-verification.json` / `.txt` | `dokimos.requirement-verification/1`: the verdict for every obligation |
| `ordo/`, `ordo-schedule.json` | The same verdicts as `causa.acceptance-evidence/1` plus a verification plan, and Ordo's ranked corrective work (`ordo verification schedule`, unchanged Ordo 1.20.0) |
| `mutations.json` / `.txt` | `causa.requirement-mutations/1`: deliberate defects injected into the served page and the probes, and whether each was caught |
| `timings.txt` | Measured wall-clock durations of each stage on this machine |

## Result

**Not accepted.** 10 of 17 applicable requirements are verified (58.8%).
14 of 17 are implemented, meaning every V1 obligation passes. INV-003 does
not apply: no domain profile can be selected.

| Status | Requirements |
|---|---|
| PASS | INV-004, INV-005, INV-006, INV-009, INV-010, INV-013, INV-014, INV-015, INV-017, INV-018 |
| FAIL, lifecycle | INV-001 problem statement and INV-002 title (entered, but not in the saved investigation); INV-008 original statement (not shown after reopening) |
| FAIL, structural | INV-007 creation timestamp, INV-011 hypothesis explanation, INV-012 evidence original content: none exists |
| FAIL, structural (accessibility) | INV-016: the visible save-state lozenge is a `<span>` with no semantic role |
| NOT_APPLICABLE | INV-003 (fact `domain-profile-selected` is false) |

The lifecycle failures have one cause. The frozen IF41-DOMAIN `EventPayload`
carries only the event kind, so a problem statement, title or creation time
has nowhere to go in the saved investigation. Fixing that changes the frozen
investigation data contract and needs the owner's decision. It is recorded
as such, not worked around.

## The verifier catches deliberate defects

`tools/requirement-mutations.fsx` was run at `35bb187` against the real
deployed page, scoped to the requirement each case targets
(`mutations.json`). Every case was caught:

| Run | Defect | Requirement | Verdict |
|---|---|---|---|
| M0 | none: the slice is implemented correctly | INV-006 | accepted (exit 0), PASS |
| M1 | the required field removed | INV-006 | not accepted (exit 4); `V1/exists` FAIL |
| M2 | the ID on an unrelated hidden `<span>` | INV-006 | not accepted; `V1/exists` FAIL |
| M3 | the field rendered disabled | INV-006 | not accepted; `V3/editable` FAIL |
| M4 | an ID duplicated in one scope | INV-004 | not accepted; `V1/exists` FAIL (ambiguous) |
| M5 | the requirement's validation test removed | INV-006 | not accepted; `V4/validation` NOT_RUN |
| M6 | evidence from tests marked not executed | INV-006 | not accepted; `V3/enter` NOT_RUN |

The Dokimos unit tests (`RequirementVerificationTests`) also cover:

- a field that accepts input but does not save;
- the wrong value saved;
- an invalid manifest;
- a stale manifest;
- a tampered manifest;
- a test-infrastructure failure.

The Praxis tests (`RequirementManifestTests`) cover the stale-manifest and
invalid-catalog cases at compilation.
