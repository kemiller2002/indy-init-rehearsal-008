"""Build the causa.acceptance-ledger/2 for WI-R8-T34 from the T34 matrix, then the
causa.acceptance-evidence-manifest/1 bundle for every PASS item.

Usage: python3 build-ledger.py   (run from anywhere; paths are relative to this file)
Inputs: ../T34-matrix.json, sources.json, preservation-gate.result.json, the pinned baseline.
Outputs: ../ledger.json, ../acceptance-evidence/*.json, ../evidence-manifest.json.
Nothing here decides the verdict: compute-acceptance-verdict.py does.
"""
import hashlib, json, os, collections

S = os.path.dirname(os.path.abspath(__file__))
T34 = os.path.dirname(S)
REPO = os.path.abspath(os.path.join(T34, '..', '..'))
INDY = '/home/user/Indy-init'
BASELINE = f'{INDY}/requirements/causa-requirements-baseline-v4.json'
APPROVALS = f'{INDY}/kickoff/rehearsal-007/approvals.causa.json'

def sha_file(p):
    return hashlib.sha256(open(p, 'rb').read()).hexdigest()

matrix = json.load(open(f'{T34}/T34-matrix.json'))
src = json.load(open(f'{S}/sources.json'))
gate = json.load(open(f'{S}/preservation-gate.result.json'))
baseline = json.load(open(BASELINE))
SHA = matrix['candidate']
RUN_ID = f'indy-init-rehearsal-008/WI-R8-T34/{SHA[:7]}'

# ---- requirements (all 161 baseline P0) ----
by_req = collections.defaultdict(list)
for row in matrix['obligations']:
    by_req[row['requirement']].append(row)

evidence_files = {}  # relative path -> dict

def evidence(subject_type, subject_id, command, etype, extra):
    rel = f'acceptance-evidence/{subject_type}-{subject_id}.json'.replace('§', '_S_').replace('/', '/', 1)
    rel = 'acceptance-evidence/' + f'{subject_type}-{subject_id}'.replace('/', '_').replace('§', '_S_') + '.json'
    evidence_files[rel] = {
        'schema': 'causa.acceptance-evidence/1', 'run_id': RUN_ID, 'source_sha': SHA,
        'subject_type': subject_type, 'subject_id': subject_id, 'status': 'PASS', 'exit_code': 0,
        'command': command, 'evidence_type': etype, **extra}
    return rel

ci_cmd = ('GitHub Actions on ' + SHA + ': ' + ', '.join(
    f"{k} {v['run']}" for k, v in src['runs'].items() if k in ('ci', 'build-and-test', 'live')))

requirements = []
for rq in baseline['requirements']:
    if rq.get('priority') != 'P0':
        continue
    rows = by_req.get(rq['id'], [])
    sts = [r['status'] for r in rows]
    tests = sorted({t['test'] for r in rows for t in r['tests']})
    item = {'id': rq['id'], 'source_doc': rq['document'], 'priority': 'P0', 'owner': None,
            'implementation_paths': [], 'tests': tests, 'evidence_paths': []}
    owners = sorted({t.get('owner') for r in rows for t in r['tests'] if t.get('owner')})
    item['owner'] = ','.join(owners) or None
    if not rows:
        item.update(status='NOT_RUN', reason='no obligation of this requirement is in the matrix')
    elif any(s == 'failed' for s in sts):
        item.update(status='FAIL', reason='bound test failed at the candidate: ' + ', '.join(
            r['obligation'] for r in rows if r['status'] == 'failed'))
    elif all(s == 'verified' for s in sts):
        rel = evidence('requirement', rq['id'], ci_cmd, 'ci', {
            'obligations': [r['obligation'] for r in rows], 'tests': tests,
            'matrix': 'evidence/T34/T34-matrix.json'})
        item.update(status='PASS', evidence_paths=[rel], reason='every obligation verified: every bound test passed at the candidate')
    elif all(s in ('verified', 'unverified-by-design') for s in sts):
        item.update(status='BLOCKED', reason='blocked by owner decision or recorded deviation: ' + ', '.join(
            r['obligation'] for r in rows if r['status'] == 'unverified-by-design'))
    else:
        n = sum(1 for s in sts if s == 'not-run')
        item.update(status='NOT_RUN', reason=f'{n} of {len(rows)} obligations have a bound test with no result at the candidate')
    requirements.append(item)

# ---- gates ----
def req_status(prefixes):
    sel = [r for r in requirements if r['id'].startswith(prefixes)]
    c = collections.Counter(r['status'] for r in sel)
    return sel, c

dep = src['deploy']
gates = []
def g(id_, name, scope, status, reason, paths=(), verified=None):
    gates.append({'id': id_, 'name': name, 'scope': scope, 'verified_sha': verified if status == 'PASS' else None,
                  'status': status, 'evidence_paths': list(paths), 'reason': reason,
                  'started_at': None, 'completed_at': None})

g('G0', 'Scope', 'praxis requirements preservation verify at acceptance (RDP-004)',
  'BLOCKED' if gate['exit_code'] != 0 else 'PASS',
  f"preservation gate re-run exit {gate['exit_code']}: {gate['stderr'].strip()} (RDP-005: exit 2 is UNKNOWN, acceptance BLOCKED). Evidence: evidence/T34/raw/preservation-gate.result.json")
own = matrix['suiteTotals']['SU-OWN-DOTNET']
g('G1', 'Dependencies', 'verified package/SHA/integrity lock, SDK, WASM/Forma/Folio smoke', 'NOT_RUN',
  'no G1 gate evidence record was produced in Rehearsal 8; CI builds with the locked set, but no integrity-lock or Forma/Folio smoke record exists')
g('G2', 'Interfaces', 'frozen handoff manifest and producer/consumer tests',
  'BLOCKED' if own['not-run'] else ('FAIL' if own['fail'] else 'PASS'),
  f"SU-OWN-DOTNET: {own['pass']} pass, {own['fail']} fail, {own['not-run']} not run (producer/consumer identities absent from source)")
inv = matrix['suiteTotals']['SU-DOTNET-INVARIANTS']
g('G3', 'Domain', 'F# unit tests, illegal-transition and forged-history variants',
  'FAIL' if inv['fail'] else ('NOT_RUN' if inv['not-run'] else 'PASS'),
  f"SU-DOTNET-INVARIANTS: {inv['pass']} pass, {inv['fail']} fail, {inv['not-run']} not run" + (' (INV04 RequestBounds, DEV-R8-11)' if inv['fail'] else ''))
g('G4', 'Frozen data', 'five golden scenarios, hashes, negative fixtures', 'NOT_RUN',
  'no G4 gate evidence record: T15/T15B replay 5/5 golden and 40/40 negatives in the contract tests; no gate record against the 90-fixture set')
for gid, name, scope, pre in (('G5', 'UI', 'P0 screen contracts, startup/error, a11y, projection fidelity', ('UI-', 'WASM-LOAD-', 'WDL-')),
                              ('G6', 'Persistence', 'local vs remote truth, expected-version conflict, reload', ('PERSIST-',))):
    sel, c = req_status(pre)
    st = 'FAIL' if c['FAIL'] else ('PASS' if sel and c['PASS'] == len(sel) else 'NOT_RUN')
    g(gid, name, scope, st, f"{len(sel)} P0 requirements: " + ', '.join(f'{k} {v}' for k, v in sorted(c.items())))
g('G7', 'Defect class', 'independent review and SHA-bound closure', 'FAIL',
  'open HIGH defect classes (see defect_classes): unwritten bound tests; INV04 (DEV-R8-11) awaits an owner decision')
missing = [k for k, v in matrix['suiteTotals'].items() if v['pass'] == 0 and v['fail'] == 0 and k not in ('SU-REVIEW-RECORDS',)]
fails = [k for k, v in matrix['suiteTotals'].items() if v['fail']]
g('G8', 'Candidate', 'full applicable browser/integration/security/report tests', 'FAIL',
  f"required suites with no result: {', '.join(missing) or 'none'}; suites with failures: {', '.join(fails) or 'none'}")
if dep.get('deployedShaVerified') and dep['jobs'].get('deployed-acceptance') == 'success':
    rel = evidence('gate', 'G9', f"deploy.yml {dep['run']} (deployed-acceptance) and curl -fsS {dep['pageUrl']}deployed-sha.txt",
                   'deployment', {'deployRun': dep['run'], 'servedShaAtDeploy': dep.get('servedShaAtDeploy'),
                                  'independentCheck': dep.get('independentCheck')})
    g('G9', 'Deployment', 'deployed SHA equals verified SHA; deployed acceptance on the same SHA', 'PASS',
      'deploy.yml deployed-acceptance succeeded on the candidate and the independent check read the candidate SHA', [rel], SHA)
else:
    g('G9', 'Deployment', 'deployed SHA equals verified SHA; deployed acceptance on the same SHA', 'FAIL',
      f"deployed-acceptance: {dep['jobs'].get('deployed-acceptance')}; independent check: {dep.get('independentCheck')}")

defect_classes = [
    {'id': 'DC-R8-UNWRITTEN-BOUND-TESTS', 'severity': 'HIGH',
     'violated_invariant': 'every P0 obligation has its bound acceptance test written and run (VER-007)',
     'original_repro': 'T31: bootstrap stub files and absent spec files', 'variants': ['deterministic stubs', 'live stubs', 'absent chromium/webkit specs', 'tests/browser/live absent'],
     'fix_sha': None, 'reviewer_evidence': 'evidence/reviews/T33-final-review.md', 'evidence_paths': [], 'status': 'FAIL'},
    {'id': 'DC-R8-INV04-DEV-R8-11', 'severity': 'HIGH',
     'violated_invariant': 'INV-04 request bounds versus SM-SAVE T-S31/T-S32',
     'original_repro': 'Causa.Invariants.Property.INV04.RequestBounds counterexample (seed 1)', 'variants': [],
     'fix_sha': None, 'reviewer_evidence': None, 'evidence_paths': [], 'status': 'BLOCKED'},
    {'id': 'T33-F-01', 'severity': 'HIGH',
     'violated_invariant': 'PERSIST-007-O1 read-back confirms files above 1 MB',
     'original_repro': 'evidence/reviews/T33-final-review.md T33-F-01', 'variants': ['save read-back', 'reconciliation'],
     'fix_sha': 'c16ea27 (WI-R8-T32L, merged in ' + SHA + ')', 'reviewer_evidence': None, 'evidence_paths': [],
     'status': 'BLOCKED'},
    {'id': 'T33-F-08', 'severity': 'HIGH',
     'violated_invariant': 'INV-06 and INV-21 pass in WebKit',
     'original_repro': 'evidence/reviews/T33-final-review.md T33-F-08', 'variants': [],
     'fix_sha': '09c7c27 (CR-T33-F08, PR #50)', 'reviewer_evidence': None, 'evidence_paths': [],
     'status': 'BLOCKED'},
]

m = src.get('metrics', {})
ledger = {
    'schema': 'causa.acceptance-ledger/2',
    'run_id': RUN_ID,
    'source_commit_sha': SHA,
    'deployed_commit_sha': dep.get('servedShaAtDeploy') if dep.get('deployedShaVerified') else None,
    'contract_version': 'IF41 / indy-init.r8-interface-manifest 1.0.0',
    'frozen_fixture_digest': 'sha256:' + sha_file(f'{REPO}/tests/fixtures/manifest.json') + ' (tests/fixtures/manifest.json)',
    'dependency_lock_sha': 'sha256:' + sha_file(f'{REPO}/package-lock.json') + ' (package-lock.json); sha256:' + sha_file(f'{REPO}/Directory.Packages.props') + ' (Directory.Packages.props)',
    'interface_manifest_sha': 'sha256:' + sha_file(f'{REPO}/evidence/interface-manifest.json') + ' (evidence/interface-manifest.json)',
    'requirements_baseline': {'id': baseline['id'], 'repository': baseline['source']['repository'],
                              'commit': baseline['source']['commit'], 'sha256': sha_file(BASELINE)},
    'approvals_sha256': sha_file(APPROVALS),
    'evidence_manifest_sha256': None,
    'scope_changes': [],
    'requirements': requirements,
    'defect_classes': defect_classes,
    'gates': gates,
    'metrics': m,
    'unresolved_decisions': [
        {'id': 'DEV-R8-11', 'owner': 'kemiller2002', 'impact': 'INV04 RequestBounds fails; PERSIST-016-O5', 'disposition': 'PENDING'},
        {'id': 'AR-008-O2/AR-024-O1/AR-024-O2', 'owner': 'kemiller2002', 'impact': 'need an owner-approval record; none exists', 'disposition': 'PENDING'},
        {'id': 'DEV-R8-04', 'owner': 'kemiller2002', 'impact': 'central FSharp.Core entry', 'disposition': 'PENDING'},
        {'id': 'DEV-R8-05', 'owner': 'kemiller2002', 'impact': 'forma-workflow 1.2.0 not applied', 'disposition': 'PENDING'},
        {'id': 'DEV-R8-09/DEV-R8-10', 'owner': 'kemiller2002', 'impact': 'frozen-contract gaps absorbed in implementation', 'disposition': 'PENDING'},
    ],
    'accepted': False,
}

os.makedirs(f'{T34}/acceptance-evidence', exist_ok=True)
entries = []
for rel, body in sorted(evidence_files.items()):
    raw = (json.dumps(body, indent=1, ensure_ascii=False) + '\n').encode('utf-8')
    open(f'{T34}/{rel}', 'wb').write(raw)
    entries.append({'path': rel, 'sha256': hashlib.sha256(raw).hexdigest()})
manifest = {'schema': 'causa.acceptance-evidence-manifest/1', 'run_id': RUN_ID, 'source_sha': SHA, 'entries': entries}
mraw = (json.dumps(manifest, indent=1) + '\n').encode('utf-8')
open(f'{T34}/evidence-manifest.json', 'wb').write(mraw)
ledger['evidence_manifest_sha256'] = hashlib.sha256(mraw).hexdigest()
json.dump(ledger, open(f'{T34}/ledger.json', 'w'), indent=1, ensure_ascii=False)
print(json.dumps({'requirements': dict(collections.Counter(r['status'] for r in requirements)),
                  'gates': {x['id']: x['status'] for x in gates}, 'evidence': len(entries),
                  'manifest_sha256': ledger['evidence_manifest_sha256'], 'baseline_sha256': ledger['requirements_baseline']['sha256'],
                  'approvals_sha256': ledger['approvals_sha256']}, indent=1))
