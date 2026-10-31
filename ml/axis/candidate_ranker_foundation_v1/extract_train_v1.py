# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Bind and capture source-only per-source panel candidates; no truth or training.

Run directly, never as a module. Each child processes one source, bounding the
size of the in-memory C# report. A separate review must approve the frozen
binding before the `run` command is invoked.
"""
from contextlib import ExitStack
from hashlib import sha256
import ctypes, importlib.util, json, os, sys
from ctypes import wintypes
from pathlib import Path

ROOT=Path(__file__).resolve().parents[3]
BASE=ROOT/'artifacts/goal22-runs/axis-candidate-evidence-panel-train-20261031-v9/input-manifest.json'
BASE_SHA='4a84e306abe1061900ce19a42c5b2f2268d90659b4b8ee3eeb5b80f91d0be717'
RECEIPT=ROOT/'artifacts/goal22-runs/axis-ranker-panelization-run-v2/receipt.json'
RECEIPT_SHA='2cb1df8370e9da28e762a16d05e0098ca461d2013bbd61e02bb402d3c630e571'
ADMISSION=ROOT/'artifacts/goal22-runs/axis-ranker-full-extraction-admission-v1'
OUT=ROOT/'artifacts/goal22-runs/axis-ranker-full-extraction-run-v1'
RUNTIME=ROOT/'tools/GraphReader.AxisCandidateEvidence/bin/Release/net10.0-windows/win-x64'
SELF=Path(__file__).resolve()
JOB=ROOT/'artifacts/goal22-tools/axis-roles-dev-measure-v2/job.py'
def digest(data):return sha256(data).hexdigest()
def safe(path):
    path=Path(os.path.abspath(path))
    try: parts=path.relative_to(ROOT).parts
    except ValueError:raise ValueError('path escapes project')
    current=ROOT
    for segment in ('',*parts):
        if segment:current=current/segment
        try:attributes=current.lstat().st_file_attributes
        except FileNotFoundError:continue
        if attributes&0x400:raise ValueError('reparse path')
    return path
def lock(stack,path):
    if sys.platform!='win32':raise ValueError('Windows no-writer locks required')
    path=safe(path);kernel=ctypes.WinDLL('kernel32',use_last_error=True)
    kernel.CreateFileW.argtypes=[wintypes.LPCWSTR,wintypes.DWORD,wintypes.DWORD,ctypes.c_void_p,wintypes.DWORD,wintypes.DWORD,wintypes.HANDLE]
    kernel.CreateFileW.restype=wintypes.HANDLE;kernel.CloseHandle.argtypes=[wintypes.HANDLE]
    handle=kernel.CreateFileW(str(path),0x80000000,1,None,3,0x80,None)
    if handle==ctypes.c_void_p(-1).value:raise ctypes.WinError(ctypes.get_last_error())
    stack.callback(kernel.CloseHandle,handle)
def read(path,expected):
    data=path.read_bytes()
    if digest(data)!=expected:raise ValueError('bound digest differs')
    return data
def canonical(doc):return (json.dumps(doc,sort_keys=True,indent=2,allow_nan=False)+'\n').encode()
def inventory(directory):
    """Enumerate runtime without traversing reparse directories."""
    safe(directory);result=[]
    for name in sorted(os.listdir(directory)):
        path=safe(directory/name)
        if path.is_dir():result.extend(inventory(path))
        elif path.is_file():result.append(path)
        else:raise ValueError('runtime entry is not a file')
    return result
def selections(receipt):
    if receipt['truth_read'] or receipt['dev_read'] or receipt['reserved_read'] or receipt['optimizer_steps'] or receipt['training_admitted']:raise ValueError('receipt scope differs')
    rows=receipt['sources'];ids=set();pairs=set();result=[]
    if len(rows)!=64:raise ValueError('source count differs')
    for row in rows:
        sid=row['id']
        if sid in ids or row['source_status'] not in ('detected','no_panel_fallback','failed'):raise ValueError('source inventory differs')
        ids.add(sid);selected=[]
        for panel in row['panels']:
            pair=(sid,panel['panel_id'])
            if pair in pairs:raise ValueError('duplicate panel')
            pairs.add(pair);selected.append({'source_id':sid,'panel_id':panel['panel_id']})
        result.append((sid,selected))
    if len(pairs)!=96:raise ValueError('panel count differs')
    return result
def freeze():
    if ADMISSION.exists():raise ValueError('admission exists')
    with ExitStack() as stack:
        lock(stack,BASE);base=json.loads(read(BASE,BASE_SHA));lock(stack,RECEIPT);receipt=json.loads(read(RECEIPT,RECEIPT_SHA))
        groups=selections(receipt)
        if base['panelizationReceiptSha256']!=RECEIPT_SHA or base['scope']!='public_synthetic_train' or base['split']!='train':raise ValueError('base binding differs')
        files={}
        for key in ('productionSourceBindings','panelizerRuntimeBindings','toolSourceBindings','toolRuntimeBindings'):
            for name,expected in base[key].items():
                if name in files and files[name]!=expected:raise ValueError('conflicting source bindings')
                files[name]=expected
        for path in inventory(RUNTIME):
            name=path.relative_to(ROOT).as_posix();lock(stack,path);h=digest(path.read_bytes())
            if name in files and files[name]!=h:raise ValueError('runtime differs')
            files[name]=h
        for path in (SELF,JOB):files[path.relative_to(ROOT).as_posix()]=digest(path.read_bytes())
        files[BASE.relative_to(ROOT).as_posix()]=BASE_SHA
        files[RECEIPT.relative_to(ROOT).as_posix()]=RECEIPT_SHA
        panel_admission=safe(ROOT/base['panelizationAdmissionBindingPath'])
        lock(stack,panel_admission)
        panel_doc=json.loads(read(panel_admission,base['panelizationAdmissionBindingSha256']))
        files[panel_admission.relative_to(ROOT).as_posix()]=base['panelizationAdmissionBindingSha256']
        if len(panel_doc['sources'])!=64:raise ValueError('source admission count differs')
        for source in panel_doc['sources']:files[source['path']]=source['sha256']
        for row in receipt['sources']:
            if row.get('panelization_sha256'):
                p=ROOT/'artifacts/goal22-runs/axis-ranker-panelization-run-v2'/row['id']/'panelization/panelization.json';files[p.relative_to(ROOT).as_posix()]=row['panelization_sha256']
            for panel in row['panels']:
                p=ROOT/'artifacts/goal22-runs/axis-ranker-panelization-run-v2'/row['id']/'panelization'/panel['path'];files[p.relative_to(ROOT).as_posix()]=panel['sha256']
        for name,h in files.items():p=safe(ROOT/name);lock(stack,p);read(p,h)
        ADMISSION.mkdir();manifests=[]
        for i,(sid,selected) in enumerate(groups):
            doc=dict(base,selectedPanels=selected);raw=canonical(doc);p=ADMISSION/f'{i:03}.json'
            with p.open('xb') as f:f.write(raw)
            manifests.append({'source_id':sid,'name':p.name,'sha256':digest(raw),'panels':selected})
        doc={'schema':'graphreader.axis-ranker-full-extraction-binding.v1','base_manifest_sha256':BASE_SHA,'receipt_sha256':RECEIPT_SHA,'files':files,'runtime_inventory':[p.relative_to(ROOT).as_posix() for p in inventory(RUNTIME)],'manifests':manifests,'source_count':64,'panel_count':96,'annotations_read':False,'dev_pixels_read':False,'reserved_read':False,'optimizer_steps':0,'training_admitted':False,'child_memory_bytes':2147483648,'timeout_seconds_per_source':120,'limits':['One source per child; empty and failed panels remain in denominators.','Python interpreter, standard library, OS and installed .NET shared framework are not attested.','Capture only; production parity and training admission remain separate.']}
        raw=canonical(doc)
        with (ADMISSION/'binding.json').open('xb') as f:f.write(raw)
        return digest(raw)
def run(expected):
    if OUT.exists():raise ValueError('run exists')
    with ExitStack() as stack:
        binding_path=ADMISSION/'binding.json';lock(stack,binding_path);binding=json.loads(read(binding_path,expected))
        if binding['schema']!='graphreader.axis-ranker-full-extraction-binding.v1' or any(binding[k] is not False for k in ('annotations_read','dev_pixels_read','reserved_read','training_admitted')) or binding['optimizer_steps']!=0:raise ValueError('scope differs')
        for name,h in binding['files'].items():p=safe(ROOT/name);lock(stack,p);read(p,h)
        for row in binding['manifests']:p=ADMISSION/row['name'];lock(stack,p);read(p,row['sha256'])
        if [p.relative_to(ROOT).as_posix() for p in inventory(RUNTIME)]!=binding['runtime_inventory']:raise ValueError('runtime inventory differs')
        spec=importlib.util.spec_from_file_location('bound_job',JOB);job=importlib.util.module_from_spec(spec);spec.loader.exec_module(job)
        safe(OUT.parent);OUT.mkdir();results=[]
        for i,item in enumerate(binding['manifests']):
            report=OUT/f'{i:03}.json';relative=report.relative_to(ROOT).as_posix()
            code,status=job.launch([RUNTIME/'GraphReader.AxisCandidateEvidence.exe','--panel-input-manifest',(ADMISSION/item['name']).relative_to(ROOT).as_posix(),'--panel-input-sha256',item['sha256'],'--output',relative],OUT/f'{i:03}.log',120)
            row={'source_id':item['source_id'],'exit_code':code,'process_status':status,'expected_panels':item['panels'],'report_sha256':None,'report_status':'missing'}
            if report.exists():
                raw=report.read_bytes();doc=json.loads(raw);actual={(r['source_id'],r['panel_id']) for r in doc['panel_results']};expected_panels={(r['source_id'],r['panel_id']) for r in item['panels']}
                if doc['input_manifest_sha256']!=item['sha256'] or len(doc['panel_results'])!=len(actual) or not actual.issubset(expected_panels):raise ValueError('panel result coverage differs')
                if doc['status'] in ('success','completed_with_failures') and (doc['source_count']!=64 or doc['panel_count']!=96 or doc['selected_panel_count']!=len(expected_panels) or actual!=expected_panels):raise ValueError('completed report coverage differs')
                row.update(report_sha256=digest(raw),report_status=doc['status'],panel_statuses=[{'panel_id':r['panel_id'],'status':r['status']} for r in doc['panel_results']],missing_panel_ids=sorted(p for _,p in expected_panels-actual))
            results.append(row);print('exported',i+1,'of 64',flush=True)
        for name,h in binding['files'].items():read(ROOT/name,h)
        read(binding_path,expected)
        if [p.relative_to(ROOT).as_posix() for p in inventory(RUNTIME)]!=binding['runtime_inventory']:raise ValueError('runtime inventory changed')
        doc={'schema':'graphreader.axis-ranker-full-extraction-receipt.v1','binding_sha256':expected,'sources':results,'source_count':64,'panel_count':96,'truth_read':False,'dev_read':False,'reserved_read':False,'optimizer_steps':0,'training_admitted':False,'limits':binding['limits']}
        raw=canonical(doc)
        with (OUT/'receipt.json').open('xb') as f:f.write(raw)
        return digest(raw)
if __name__=='__main__':
    if sys.argv[1:]==['freeze']:print('extraction_binding_sha256',freeze())
    elif len(sys.argv)==3 and sys.argv[1]=='run':print('extraction_receipt_sha256',run(sys.argv[2]))
    else:raise SystemExit('usage: extract_train_v1.py freeze | run <reviewed-binding-sha>')
