"""Write evidence/T34/T34-ACCEPTANCE-REPORT.md from the matrix, ledger, sources and verdict output.
Usage: python3 gen-report.py VERDICT   (VERDICT is the value computed by compute-acceptance-verdict.py)"""
import json, os, sys, collections
S = os.path.dirname(os.path.abspath(__file__)); T = os.path.dirname(S)
m = json.load(open(f'{T}/T34-matrix.json')); L = json.load(open(f'{T}/ledger.json'))
src = json.load(open(f'{S}/sources.json')); prev = json.load(open(f'{S}/previous-candidate-c61bfe2.json'))
live = json.load(open(f'{S}/live-attempts.json'))
V = sys.argv[1]
SHA = m['candidate']; st = m['suiteTotals']
rows = m['obligations']; c = collections.Counter(r['status'] for r in rows)
failed = [r['obligation'] for r in rows if r['status'] == 'failed' or (r['status'] == 'unverified-by-design' and any(t['result'] == 'fail' for t in r['tests']))]
ubd_nr = [r['obligation'] for r in rows if r['status'] == 'unverified-by-design' and r['obligation'] not in failed]
ver = c['verified']; notrun = c['not-run'] + len(ubd_nr)
by = collections.defaultdict(list)
for r in rows: by[r['requirement']].append(r['status'])
full = sorted(k for k, v in by.items() if all(s == 'verified' for s in v))
pc = prev['counts']['P0']; pfail = 1; pver = pc.get('verified', 0); pnr = 613 - pver - pfail
U = 'https://github.com/kemiller2002/indy-init-rehearsal-008/actions/runs/'
def cause(t):
    s = t['suite']; r = t.get('reason', '')
    if t['result'] == 'fail':
        return f"failed bound test: `{t['test']}`" + (' (DEV-R8-11, owner decision)' if 'INV04' in t['test'] else '')
    if s in ('SU-DOTNET-DETERMINISTIC', 'SU-DOTNET-LIVE', 'SU-OWN-DOTNET'): return f'{s}: bound .NET test absent (bootstrap stub or missing identity)'
    if s in ('SU-BROWSER-CHROMIUM', 'SU-BROWSER-WEBKIT'):
        return f"{s}: " + ('spec file absent' if 'absent' in r else ('identity drift' if 'drift' in r else 'spec exists, bound title did not run'))
    if s == 'SU-BROWSER-LIVE': return 'SU-BROWSER-LIVE: tests/browser/live does not exist'
    if s == 'SU-VERIFIER': return 'SU-VERIFIER: no independent verifier job'
    if s == 'SU-PROCESS-RECORDS': return 'SU-PROCESS-RECORDS: no Praxis process evidence of the bound kind'
    if t['test'] == 'praxis:owner-approval': return 'owner decision: no owner-approval record'
    return f'{s}: {r[:60]}'
groups = collections.defaultdict(set)
for r in rows:
    if r['status'] == 'verified': continue
    for t in r['tests']:
        if t['result'] != 'pass': groups[cause(t)].add(r['obligation'])
gates = {g['id']: g for g in L['gates']}
dep = src['deploy']; runs = src['runs']
reqc = collections.Counter(r['status'] for r in L['requirements'])
o = []; w = o.append
w('# T34 final acceptance: preservation gate, ledger, computed verdict, measurements\n')
w(f"- Work item: WI-R8-T34 (integration lane), regenerated for the final candidate. Generated {src['generatedAt']}.")
w(f"- **Candidate: `{SHA}`**. {src['candidateSource']}.")
w(f"- **Previous candidate: `c61bfe2`** (PR #53): {pver} verified, {pfail} failed, {pnr} not run of 613. Computed verdict {prev['verdict']}. Its raw inputs are kept in `evidence/T34/raw/c61bfe2/` and `raw/previous-candidate-c61bfe2.json`.")
w("- Plan: v4 `VP-1e3c66bcc958`, which has 613 obligations, all P0 and none P1. It runs through `requirements-compiled/v4.1.1/verification-execution-plan.json`: 11 suites, 660 plan tests plus 140 of our own.")
w("- Method: the same as T31's, regenerated at the candidate with the same scripts and rules. Results come only from CI runs on the candidate: the TRX artifacts for .NET and the Playwright list reporter in the job logs for the browser suites. An obligation counts as verified only when every test identity bound to it passed at the candidate SHA. Nothing was estimated, and no suite was run locally.")
w("- Files:")
w("  - `T34-matrix.json`: one row per obligation.")
w("  - `ledger.json`: `causa.acceptance-ledger/2`.")
w("  - `evidence-manifest.json` plus `acceptance-evidence/`: `causa.acceptance-evidence-manifest/1`.")
w("  - `raw/`: scripts, parsed CI results, gate run, deployment checks and verdict output.")
w("- Parser fix in this regeneration: `raw/pwlog.py` now accepts Playwright durations in minutes, such as `(1.3m)`. Before the fix, one WebKit title that ran for 1.3 minutes was dropped. No c61bfe2 log line has such a duration, so the previous numbers are unaffected.\n")
w('## Computed verdict\n')
w(f'<!-- acceptance-verdict: {V} -->\n')
w(f"`tools/compute-acceptance-verdict.py` computed **{V}** (exit 3). This is the only verdict in this report. **The candidate cannot be released.**\n")
w("The verdict tool reports INVALID because one of its trust inputs fails before it compares the ledger: the pinned approvals file. That file is `kickoff/rehearsal-007/approvals.causa.json`, the latest one, and it holds APR-V411-OWNER-1/2. It fails in two ways:")
w("- No approval names an immutable authority source (`source.repository/commit/path`).")
w("- The changes do not use the `requirement_id` and `action` identity that the verifier expects.\n")
w("Only the owner can fix this; no agent writes approvals.\n")
w(f"Even with valid trust inputs, the ledger would compute FAILED:")
w(f"- Gates: " + ', '.join(f"{k} {g['status']}" for k, g in gates.items()) + '.')
w(f"- P0 requirements: " + ', '.join(f'{v} {k}' for k, v in sorted(reqc.items())) + '.\n')
w("Exact command: `evidence/T34/raw/run-verdict.sh`. Output: `raw/verdict.json`. Input digests: `raw/verdict-inputs.txt`. The inputs are:")
w(f"- Baseline: `requirements/causa-requirements-baseline-v4.json`, sha256 `{L['requirements_baseline']['sha256']}`, source `{L['requirements_baseline']['commit']}`.")
w(f"- Approvals: sha256 `{L['approvals_sha256']}`.")
w(f"- Trusted operator: `kemiller2002`.")
w(f"- Evidence manifest: sha256 `{L['evidence_manifest_sha256']}`.")
w(f"- Trusted source SHA: `{SHA}`.\n")
w('## Counts (P0; the frozen plan has no P1 obligations)\n')
w('| Status | Obligations | Previous (c61bfe2) |\n|---|---|---|')
w(f'| verified (every bound test passed at the candidate) | **{ver}** | {pver} |')
w(f"| failed (a bound test failed) | **{len(failed)}**: {', '.join(failed)} | {pfail} |")
w(f"| not run | **{notrun}**: {c['not-run']} with a bound test that produced no result, plus {len(ubd_nr)} where only the owner-approval record is missing ({', '.join(ubd_nr)}) | {pnr} |")
w(f'| **total** | **{len(rows)}** | 613 |\n')
w(f"Requirements with every obligation verified: **{len(full)} of 161** P0 (c61bfe2: 50). They are: {', '.join(full)}.\n")
w(f"Test identities: {m['counts']['testIdentities']}.\n")
w('### Live suite: attempt 1 and the single permitted re-run\n')
w(live['summary'] + '\n')
w('### Rules applied (T33 review)\n')
w("- **PERSIST-007-O1:** " + live['persist007'])
w("- **WebKit:** any failure in the WebKit job would count as failed. The job had no failures and no retries. INV-06 (`every store empty of investigation content [webkit]`) and INV-21 (`no credential in storage, URL or artifact [webkit]`) passed at the candidate.")
w("- **Owner decisions:** INV04 is DEV-R8-11, an owner decision. AR-008-O2 and AR-024-O1/O2 have no owner-approval record. The T33 review record counts for `praxis:independent-review`; it ratifies no owner decision.\n")
w('## Per-suite results\n')
w('| Suite | Planned | Pass | Fail | Not run | Previous pass/fail (c61bfe2) | Evidence |\n|---|---|---|---|---|---|---|')
for k, v in st.items():
    p = prev['suiteTotals'][k]
    w(f"| {k} | {v['planned']} | {v['pass']} | {v['fail']} | {v['not-run']} | {p['pass']}/{p['fail']} | {src['suites'][k]['evidence'] or 'none'} |")
w('\nNot-run causes: bootstrap stub files (deterministic and live .NET), spec files that are absent or titles that did not run (chromium and webkit), `tests/browser/live` being absent, no SU-VERIFIER job, no Praxis process records, and 24 SU-OWN-DOTNET producer/consumer identities absent from source.\n')
w('## CI runs on the candidate\n')
w('| Workflow | Run | Result |\n|---|---|---|')
for k, v in runs.items():
    w(f"| {k} | {v['run']} | {v['conclusion']} |")
w('\n## Deployment verification\n')
w(f"- **deploy.yml deployed-acceptance** ({dep['deployedAcceptanceJob']}): success. The job reads the served `deployed-sha.txt` until it equals `GITHUB_SHA`. It then checks `data-engine=\"loading\"`, `_framework/dotnet.js` and the limen bundle. Its log prints `Deployed {SHA} at https://kevinmmiller.us/indy-init-rehearsal-008` (`{dep['deployedAcceptanceLog']}`).")
chk = open(f"{S}/deployed-check-5e27176.log").read().strip().splitlines()
w(f"- **Independent curl check by T34** (`raw/deployed-check-5e27176.log`): " + '; '.join(chk) + '.')
w(f"- **The deployed SHA equals the candidate.** Ledger gate G9 is PASS, with deployment evidence in the manifest. The deployed-live browser suite cannot run because its tests do not exist.\n")
w('## Live (real-provider) evidence\n')
w(live['evidence'] + '\n')
w('## Preservation gate (re-run at acceptance, RDP-004)\n')
w("**Result: exit 2 (UNKNOWN). Under RDP-005 that is BLOCKED, so ledger G0 is BLOCKED.** The candidate change (tests only) does not alter the gate inputs, so the run in `raw/preservation-gate.result.json` still applies. That file records the exact command, its stderr and the input SHA-256s. The run fails with `ERROR file 'research/requirements-preservation/disposition.json' does not exist`.")
w("- Rehearsal 8 never wrote a `praxis.requirements-disposition/1` file or a derived Praxis requirements package.")
w("- T08 has no record in `.ros`, `evidence/` or `SCHEDULE-ACTUALS.md`.")
w("- The package's `requirements-compiled/v4.1.1/preservation-report.json` reports `PRESERVED`. That report is the compiler's derivation proof, not the Praxis gate, so it is not counted as G0.\n")
w('## Ledger gates\n')
w('| Gate | Status | Basis |\n|---|---|---|')
for k, g in gates.items():
    w(f"| {k} {g['name']} | {g['status']} | {g['reason']} |")
w('\nOpen owner decisions (`unresolved_decisions`): ' + '; '.join(f"{d['id']} ({d['impact']})" for d in L['unresolved_decisions']) + '.\n')
w('## Measurements (timed-execution-manifest; unavailable values recorded as unavailable)\n')
mt = src['metrics']
w('| Measurement | Value |\n|---|---|')
w(f"| first green CI | T+{mt['first_green_elapsed_seconds']} s ({mt['first_green_note']}) |")
w(f"| first candidate | T+{mt['first_candidate_elapsed_seconds']} s ({mt['first_candidate_note']}) |")
w("| final candidate | 5e27176, merged 23:30Z (about T+21524 s) |")
w('| accepted verdict | unavailable (not accepted) |')
w(f"| candidates | {mt['candidate_count']} ({mt['candidate_note']}) |")
w(f"| CI | {mt['ci_note']} |")
w(f"| tokens per agent | unavailable in this report ({mt['token_note']}) |")
w(f"| defect-class recurrence | unavailable ({mt['repeated_defect_class_note']}) |")
w(f"| verified requirements | {len(full)} of 161 P0 |\n")
w('## T34 covers\n')
cov = {k: [r['status'] for r in rows if r['requirement'] == k] for k in ('PERSIST-001', 'PERSIST-026', 'PERSIST-055')}
w('PERSIST-001, PERSIST-026 and PERSIST-055: ' + '; '.join(f"{k} {collections.Counter(v)}" for k, v in cov.items()) + '. Their deterministic acceptance tests are stubs, and PERSIST-026-O2 also needs the SU-VERIFIER job, which does not exist. For the R14/R16 and doc covers, T34 delivers this ledger, the gate run and the computed verdict.\n')
w(f"## Unverified obligations by cause ({len(rows)-ver} = {len(failed)} failed + {notrun} not run)\n")
w('An obligation is listed under every cause that applies to it.\n')
for k in sorted(groups, key=lambda x: -len(groups[x])):
    ids = sorted(groups[k]); w(f"- **{k}** ({len(ids)}): {', '.join(ids)}")
w('\n## Reproduce\n')
w('```\npython3 evidence/T34/raw/build-matrix.py evidence/T34/T34-matrix.json\npython3 evidence/T34/raw/build-ledger.py\npython3 <indy-init>/tools/validate-acceptance-ledger.py evidence/T34/ledger.json\nREPORT=evidence/T34/T34-ACCEPTANCE-REPORT.md bash evidence/T34/raw/run-verdict.sh\n```')
open(f'{T}/T34-ACCEPTANCE-REPORT.md', 'w').write('\n'.join(o) + '\n')
print('verified', ver, 'failed', len(failed), 'notrun', notrun, 'reqs', len(full))
