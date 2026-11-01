import sys,glob,json,xml.etree.ElementTree as ET
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
out={}
for f in sys.argv[2:]:
    r=ET.parse(f).getroot()
    defs={}
    for ut in r.iterfind('.//t:UnitTest',ns):
        tm=ut.find('t:TestMethod',ns)
        defs[ut.get('id')]=tm.get('className')+'.'+tm.get('name')
    for res in r.iterfind('.//t:UnitTestResult',ns):
        name=defs.get(res.get('testId'),res.get('testName'))
        msg=res.find('.//t:Message',ns)
        out[name]={'outcome':res.get('outcome'),'file':f.split('/')[-1],'message':(msg.text[:300] if msg is not None and msg.text else None)}
json.dump(out,open(sys.argv[1],'w'),indent=0)
from collections import Counter
print(len(out),Counter(v['outcome'] for v in out.values()))
