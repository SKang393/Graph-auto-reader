# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Join frozen train-only costs and feature snapshots. No model or optimizer."""
from contextlib import ExitStack
from hashlib import sha256
import ctypes, importlib.util, json, math, os, sys
from ctypes import wintypes
from pathlib import Path

ROOT=Path(__file__).resolve().parents[3]
TARGET_ROOT=ROOT/'artifacts/goal22-runs/axis-ranker-train-label-run-v2'
EXTRACT_ROOT=ROOT/'artifacts/goal22-runs/axis-ranker-full-extraction-run-v1'
FEATURE=ROOT/'ml/axis/candidate_ranker_foundation_v1/features.py'
OUT=ROOT/'artifacts/goal22-runs/axis-ranker-gpu-training-data-v1'
TARGET_RECEIPT_SHA='c1985f56cccba2316ef12452946ac9c3c692129a599970d4286984f898a85c15'
TARGET_SHA='15193d25c5346799d16aadbaaffb303cdcb0b361aa2a7ebdedf336d706289582'
EXTRACT_SHA='588f1375544477505864a228922452266c396b0008e2d66efe20beb56f95bd58'
FEATURE_SHA='057d7dc587cf0b16fff2b9aead0908f6dedaa8cab7ec135611bae276728c0558'
def sha(raw):return sha256(raw).hexdigest()
def safe(path):
    path=Path(os.path.abspath(path))
    try:parts=path.relative_to(ROOT).parts
    except ValueError:raise ValueError('path escapes project')
    current=ROOT
    for part in ('',*parts):
        if part:current=current/part
        try:attributes=current.lstat().st_file_attributes
        except FileNotFoundError:continue
        if attributes&0x400:raise ValueError('reparse path')
    return path
def lock(stack,path):
    if sys.platform!='win32':raise ValueError('Windows no-writer locks required')
    path=safe(path);k=ctypes.WinDLL('kernel32',use_last_error=True)
    k.CreateFileW.argtypes=[wintypes.LPCWSTR,wintypes.DWORD,wintypes.DWORD,ctypes.c_void_p,wintypes.DWORD,wintypes.DWORD,wintypes.HANDLE];k.CreateFileW.restype=wintypes.HANDLE;k.CloseHandle.argtypes=[wintypes.HANDLE]
    h=k.CreateFileW(str(path),0x80000000,1,None,3,0x80,None)
    if h==ctypes.c_void_p(-1).value:raise ctypes.WinError(ctypes.get_last_error())
    stack.callback(k.CloseHandle,h)
def checked(path,expected):
    raw=path.read_bytes()
    if sha(raw)!=expected:raise ValueError('frozen input digest differs')
    return json.loads(raw)
def join_group(panel,target,features):
    if panel['status']!='success' or panel['source_sha256']!=target['source_sha256'] or panel['panel_sha256']!=target['panel_sha256']:raise ValueError('panel identity differs')
    snapshots=panel['candidate_snapshots'];matrix=panel['normalized_features']['rows'];truth=target['rows']
    derived=features.snapshot_group(panel['source_sha256'],panel['panel_sha256'],panel['panel_width'],panel['panel_height'],snapshots)['rows']
    if len(snapshots)!=len(matrix) or len(matrix)!=len(truth) or len(truth)!=len(derived) or len(truth)<2:raise ValueError('candidate count differs')
    output=[]
    for i,(snap,observed,labeled,calculated) in enumerate(zip(snapshots,matrix,truth,derived,strict=True)):
        if snap['Rank']!=i or observed['rank']!=i+1 or labeled['rank']!=i+1 or calculated['rank']!=i+1 or labeled['candidate_identity_sha256']!=calculated['candidate_identity_sha256']:raise ValueError('candidate identity/rank differs')
        if len(observed['features'])!=17 or any(not math.isclose(a,b,rel_tol=1e-12,abs_tol=1e-12) for a,b in zip(observed['features'],calculated['features'],strict=True)):raise ValueError('feature parity differs')
        if isinstance(labeled['target_error'],bool) or not isinstance(labeled['target_error'],(int,float)) or not math.isfinite(labeled['target_error']) or labeled['target_error']<0:raise ValueError('target cost differs')
        output.append({'rank':i+1,'candidate_identity_sha256':calculated['candidate_identity_sha256'],'features':observed['features'],'target_error':labeled['target_error']})
    return {'source_sha256':panel['source_sha256'],'panel_sha256':panel['panel_sha256'],'rows':output}
def build():
    if OUT.exists():raise ValueError('output exists')
    with ExitStack() as stack:
        for p in (TARGET_ROOT/'receipt.json',TARGET_ROOT/'targets.json',EXTRACT_ROOT/'receipt.json',FEATURE):lock(stack,p)
        label=checked(TARGET_ROOT/'receipt.json',TARGET_RECEIPT_SHA);targets=checked(TARGET_ROOT/'targets.json',TARGET_SHA);extract=checked(EXTRACT_ROOT/'receipt.json',EXTRACT_SHA)
        if label['targets_sha256']!=TARGET_SHA or label['dev_read'] or label['reserved_read'] or label['optimizer_steps'] or label['training_admitted'] or extract['truth_read'] or extract['dev_read'] or extract['reserved_read'] or extract['optimizer_steps']:raise ValueError('scope differs')
        if sha(FEATURE.read_bytes())!=FEATURE_SHA:raise ValueError('feature source differs')
        spec=importlib.util.spec_from_file_location('bound_axis_features',FEATURE);feature=importlib.util.module_from_spec(spec);spec.loader.exec_module(feature)
        by_key={(g['source_sha256'],g['panel_sha256']):g for g in targets['groups']}
        if len(by_key)!=93 or len(targets['groups'])!=93 or len(extract['sources'])!=64:raise ValueError('training group inventory differs')
        output=[];used=set()
        for i,row in enumerate(extract['sources']):
            if row['process_status']!='completed' or row['exit_code']!=0 or row['report_status']!='success':raise ValueError('export source did not succeed')
            report=EXTRACT_ROOT/f'{i:03}.json';lock(stack,report);doc=checked(report,row['report_sha256'])
            for panel in doc['panel_results']:
                key=(panel['source_sha256'],panel['panel_sha256'])
                if key in by_key:
                    if key in used:raise ValueError('duplicate panel')
                    used.add(key);output.append(join_group(panel,by_key[key],feature))
        if used!=set(by_key):raise ValueError('not all labelable panels joined')
        doc={'schema':'graphreader.axis-ranker-training-groups.draft-v1','split':'train','feature_names':list(feature.FEATURE_NAMES),'groups':output}
        raw=(json.dumps(doc,sort_keys=True,separators=(',',':'),allow_nan=False)+'\n').encode()
        safe(OUT.parent);OUT.mkdir()
        with (OUT/'groups.json').open('xb') as f:f.write(raw)
        report={'schema':'graphreader.axis-ranker-training-data.v1','label_receipt_sha256':TARGET_RECEIPT_SHA,'targets_sha256':TARGET_SHA,'extraction_receipt_sha256':EXTRACT_SHA,'feature_source_sha256':FEATURE_SHA,'builder_sha256':sha(Path(__file__).read_bytes()),'groups_sha256':sha(raw),'groups':len(output),'candidates':sum(len(g['rows']) for g in output),'dev_read':False,'reserved_read':False,'optimizer_steps':0,'training_admitted':False,'limits':['Train-only synthetic; 3 fallback plots excluded by label admission.','Shared synthetic generator/fonts mean independence is not established.']}
        b=(json.dumps(report,sort_keys=True,indent=2)+'\n').encode()
        with (OUT/'receipt.json').open('xb') as f:f.write(b)
        return sha(b)
if __name__=='__main__':
    if len(sys.argv)!=1:raise SystemExit('usage: prepare_data.py')
    print('data_receipt_sha256',build())
