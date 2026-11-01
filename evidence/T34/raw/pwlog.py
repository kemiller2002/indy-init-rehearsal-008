# Parse Playwright "list" reporter lines from a GitHub job log into {planId: outcome}
import sys,re,json
out={}
pat=re.compile(r'\s(✓|✘|-|ok|x)\s+\d+\s+\[(\w+)\]\s+›\s+(\S+?):\d+:\d+\s+›\s+(.*?)(?:\s+\(\d+(?:\.\d+)?(?:ms|s|m|h)\))?\s*$')
for f in sys.argv[2:]:
    for line in open(f,encoding='utf-8',errors='replace'):
        line=re.sub(r'\x1b\[[0-9;]*m','',line.rstrip('\n'))
        m=pat.search(line)
        if not m: continue
        mark,proj,file,title=m.groups()
        file=re.sub(r'^(tests/browser/)','',file)
        title=title.replace(' › ',' > ')
        key=f"playwright:{file} > {title} [{proj}]"
        st={'✓':'passed','ok':'passed','✘':'failed','x':'failed','-':'skipped'}[mark]
        prev=out.get(key)
        # retries: a later pass after fail = flaky; record both
        if prev and prev!=st: st=prev+'->'+st
        out[key]=st
json.dump(out,open(sys.argv[1],'w'),indent=0)
from collections import Counter
print(len(out),Counter(out.values()))
