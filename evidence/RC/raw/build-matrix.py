import json,sys,os,collections
S=os.path.dirname(os.path.abspath(__file__))
P='/home/user/Indy-init/requirements-compiled'
plan=json.load(open(f'{P}/v4.1.1/verification-execution-plan.json'))
v4=json.load(open(f'{P}/v4/verification-plan.json'))
cfg=json.load(open(f'{S}/sources.json'))
SHA=cfg['candidate']
def load(n):
    p=f'{S}/{n}'
    return json.load(open(p)) if os.path.exists(p) else {}
dot={**load('ci-dotnet.json'),**load('ci-live.json')}
pw={**load('pw-chromium.json'),**load('pw-webkit.json')}
repo='/home/user/r8/T31'
def file_exists_for(tid):
    if tid.startswith('playwright:'):
        f=tid[len('playwright:'):].split(' > ')[0]
        return os.path.exists(f'{repo}/tests/browser/{f}')
    return None
# test -> result
suite_of={}; tests={}
for s in plan['suites']:
    for t in s['tests']:
        suite_of[t['id']]=s['id']; tests[t['id']]=t
INV04='dotnet:Causa.Invariants.Property.INV04.RequestBounds'
def result(tid):
    s=suite_of[tid]; src=cfg['suites'].get(s,{})
    ev=src.get('evidence')
    if tid.startswith('dotnet:'):
        n=tid[7:]
        if n in dot:
            o=dot[n]['outcome']
            st={'Passed':'pass','Failed':'fail'}.get(o,'not-run')
            r={'result':st,'evidence':ev}
            if st=='fail': r['detail']=(dot[n].get('message') or '')[:240]
            if tid==INV04: r['unverifiedByDesign']='DEV-R8-11 (INV-04 vs SM-SAVE T-S31/T-S32 package contradiction; owner decision pending)'
            return r
        return {'result':'not-run','reason':'test identity not present in the candidate test run (acceptance file is the bootstrap stub or the test is absent)','evidence':ev}
    if tid.startswith('playwright:'):
        if tid in pw:
            o=pw[tid]
            st='pass' if o=='passed' else ('fail' if 'failed' in o and not o.endswith('passed') else ('pass' if o.endswith('passed') else 'not-run'))
            r={'result':st,'evidence':ev}
            if '->' in o: r['detail']='flaky: '+o
            return r
        ex=file_exists_for(tid)
        if s=='SU-BROWSER-LIVE':
            return {'result':'not-run','reason':'deployed-live browser suite: tests/browser/live does not exist at the candidate','evidence':None}
        if s=='SU-BROWSER-WEBKIT' and ex:
            return {'result':'not-run','reason':'spec file exists but the test title did not run in the CI WebKit job (CI WebKit runs tests/browser/acceptance only, or the title differs)','evidence':ev}
        drift={'playwright:invariants/INV-06.spec.js > every store empty of investigation content [chromium]':'identity drift: the spec ran and passed under the title "INV-06" (same assertion intent); the plan identity did not run',
               'playwright:invariants/INV-10.spec.js > tabs independent with and without navigator.locks [chromium]':'identity drift: the spec ran and passed under the title "INV-10" and does not exercise the without-navigator.locks case; the plan identity did not run'}
        if tid in drift: return {'result':'not-run','reason':drift[tid],'evidence':ev}
        return {'result':'not-run','reason':('spec file exists but no test with this title ran' if ex else 'spec file absent at the candidate'),'evidence':ev}
    if tid.startswith('verifier:'):
        return {'result':'not-run','reason':'no independent verifier job exists at the candidate (SU-VERIFIER)','evidence':None}
    if tid=='praxis:owner-approval':
        return {'result':'not-run','unverifiedByDesign':'owner-decision: requires an owner-approval record','evidence':None}
    if tid=='praxis:independent-review':
        return {'result':'not-run','reason':'pending: independent review record is T33 (ordered after T31); no independent-review evidence recorded at the candidate','evidence':None}
    if tid.startswith('praxis:'):
        return {'result':'not-run','reason':'no Praxis work evidence of this kind in .ros at the candidate and no verify-process run','evidence':None}
    return {'result':'not-run','reason':'unknown runner'}
own=json.load(open(f'{P}/v4.1.1/agent-file-ownership.json'))['files']
ownmap={f['path']:(f.get('ownerByTopology',{}).get('C') or f['owner']) for f in own}
def owner_of(tid):
    if tid.startswith('playwright:'):
        return ownmap.get('tests/browser/'+tid[11:].split(' > ')[0])
    if tid.startswith('dotnet:'):
        parts=tid[7:].split('.')
        # Causa.Acceptance.Deterministic.AR014Tests.X -> tests/Causa.Acceptance/Deterministic/AR014Tests.fs
        for i in range(len(parts)-1,1,-1):
            for proj in ('Causa.Acceptance','Causa.Invariants','Causa.Contract.Tests','Causa.App.Tests','Causa.Domain.Tests','Causa.Persistence.Tests'):
                pre=proj.split('.')
                if parts[:len(pre)]==pre:
                    path='tests/'+proj+'/'+'/'.join(parts[len(pre):i])+'.fs'
                    if path in ownmap: return ownmap[path]
        return None
    if tid.startswith('praxis:independent-review'): return 'reviewer'
    if tid.startswith('praxis:owner-approval'): return 'owner'
    if tid.startswith('praxis:') or tid.startswith('verifier:'): return 'integration'
    return None
rank={'fail':3,'not-run':2,'blocked':2,'pass':0}
rows=[]
by_obl=collections.defaultdict(list)
for tid,t in tests.items():
    for o in t.get('obligations',[]): by_obl[o].append(tid)
v4obl={}
for rq in v4['requirements']:
    for o in rq['obligations']:
        v4obl[o['sourceObligation']]=(rq['requirement'],o)
for oid,(req,o) in v4obl.items():
    ac=o['acceptanceContract']
    tids=sorted(set(by_obl.get(oid,[])) | {t['id'] for t in ac['testIdentity']['tests']})
    trs=[]
    for tid in tids:
        if tid not in suite_of:
            trs.append({'test':tid,'suite':None,'result':'not-run','reason':'bound in v4 plan but absent from the v4.1.1 execution plan'}); continue
        trs.append({'test':tid,'suite':suite_of[tid],'owner':owner_of(tid),**result(tid)})
    ubd=sorted({t['unverifiedByDesign'] for t in trs if t.get('unverifiedByDesign')})
    core=[t for t in trs if not t.get('unverifiedByDesign')]
    res=[t['result'] for t in core]
    if any(r=='fail' for r in res): st='failed'
    elif all(r=='pass' for r in res): st='unverified-by-design' if ubd else 'verified'
    else: st='not-run'
    auto=[t for t in core if t['test'].split(':')[0] in ('dotnet','playwright','verifier')]
    sub=None
    if st=='not-run':
        if auto and all(t['result']=='pass' for t in auto): sub='automated tests pass; awaiting process/review record'
        elif not auto: sub='process/review record only'
    rows.append({'obligation':oid,'requirement':req,'priority':ac['priority'],'text':o['text'],'requiredEvidenceCategories':ac['requiredEvidenceCategories'],'status':st,'statusNote':sub,'unverifiedByDesign':ubd or None,'tests':trs})
rows.sort(key=lambda r:r['obligation'])
cnt=collections.Counter(r['status'] for r in rows)
tcnt=collections.Counter()
for tid in tests: tcnt[result(tid)['result']]+=1
out={'schema':'indy-init.r8.t31-rc-matrix/1','candidate':SHA,'planId':v4['planIdentity']['planId'],'executionPlan':'requirements-compiled/v4.1.1/verification-execution-plan.json','generatedAt':cfg['generatedAt'],
 'deploy':cfg['deploy'],'runs':cfg['runs'],'suites':cfg['suites'],
 'statusRules':{'verified':'every bound test identity passed at the candidate SHA','failed':'at least one bound test failed at the candidate SHA','not-run':'at least one bound test has no result at the candidate (missing test, suite not run, or a process/review record not yet produced); none failed','unverified-by-design':'the only non-passing bound tests are blocked by a recorded deviation (DEV-R8-11) or require an owner decision'},
 'counts':{'obligations':len(rows),'P0':dict(cnt),'P1':{'obligations':0,'note':'the frozen v4 plan scope is P0 only (613 P0 obligations, 0 P1)'},'notRunBreakdown':dict(collections.Counter(r['statusNote'] or 'some bound automated test has no result' for r in rows if r['status']=='not-run')),'testIdentities':dict(tcnt),'unverifiedByDesignAnnotated':{r['obligation']:{'status':r['status'],'reasons':r['unverifiedByDesign']} for r in rows if r['unverifiedByDesign']}},
 'obligations':rows}
json.dump(out,open(sys.argv[1],'w'),indent=1,ensure_ascii=False)
print(json.dumps(out['counts'],indent=1))
