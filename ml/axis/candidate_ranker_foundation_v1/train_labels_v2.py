# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Train-only deferred truth binding. `freeze` never opens annotations.

The `run` command is for a separately reviewed binding, after frozen source-only
exports. It cannot train or promote a model. Run directly from project root.
"""
from contextlib import ExitStack
from hashlib import sha256
import ctypes, importlib, json, math, os, sys
from ctypes import wintypes
from pathlib import Path

ROOT=Path(__file__).resolve().parents[3]
INPUT=ROOT/'artifacts/goal22-runs/axis-ranker-input-admission-v1/input-binding.json'
TRUTH=INPUT.with_name('train-truth-binding.json');TRAIN=INPUT.with_name('train-inputs.json')
CAPTURE=ROOT/'artifacts/goal22-runs/axis-ranker-full-extraction-run-v1/receipt.json'
PANEL=ROOT/'artifacts/goal22-runs/axis-ranker-panelization-run-v2/receipt.json'
AUDIT=ROOT/'artifacts/goal22-runs/axis-ranker-full-extraction-audit-v1/report.json'
ADMISSION=ROOT/'artifacts/goal22-runs/axis-ranker-train-label-admission-v2'
OUT=ROOT/'artifacts/goal22-runs/axis-ranker-train-label-run-v2'
INPUT_SHA='147cf08b51ea23640d846857cfb1339ccc892e9fdc9567b3f099d375c4e1a320'
CAPTURE_SHA='588f1375544477505864a228922452266c396b0008e2d66efe20beb56f95bd58'
PANEL_SHA='2cb1df8370e9da28e762a16d05e0098ca461d2013bbd61e02bb402d3c630e571'
AUDIT_SHA='5863b87cf592ce2046689040a50d1e9da9cb4e61fe5b051a3649066a90be84ee'
ANNOTATION_DIR='artifacts/axis-roles-training-corpus-v1-20261031/annotations/'
def sha(data):return sha256(data).hexdigest()
def canonical(doc):return (json.dumps(doc,sort_keys=True,indent=2,allow_nan=False)+'\n').encode()
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
    p=safe(path);kernel=ctypes.WinDLL('kernel32',use_last_error=True)
    kernel.CreateFileW.argtypes=[wintypes.LPCWSTR,wintypes.DWORD,wintypes.DWORD,ctypes.c_void_p,wintypes.DWORD,wintypes.DWORD,wintypes.HANDLE]
    kernel.CreateFileW.restype=wintypes.HANDLE;kernel.CloseHandle.argtypes=[wintypes.HANDLE]
    h=kernel.CreateFileW(str(p),0x80000000,1,None,3,0x80,None)
    if h==ctypes.c_void_p(-1).value:raise ctypes.WinError(ctypes.get_last_error())
    stack.callback(kernel.CloseHandle,h)
def read(path,expected):
    data=path.read_bytes()
    if sha(data)!=expected:raise ValueError('bound digest differs')
    return json.loads(data)
def validate_annotation_entries(rows):
    if not isinstance(rows,list) or len(rows)!=64:raise ValueError('expected exactly 64 annotations')
    scenes=set()
    for index,row in enumerate(rows):
        if not isinstance(row,dict) or set(row)!={'scene_id','path','sha256'}:raise ValueError('invalid annotation entry')
        if row['path']!=ANNOTATION_DIR+str(index)+'.json':raise ValueError('annotation path differs')
        if not isinstance(row['scene_id'],str) or not row['scene_id'] or row['scene_id'] in scenes:raise ValueError('duplicate or invalid annotation scene')
        scenes.add(row['scene_id'])
        h=row['sha256']
        if not isinstance(h,str) or len(h)!=64 or any(c not in '0123456789abcdef' for c in h):raise ValueError('annotation digest differs')
def validate_metadata(truth,train,capture,panel,audit):
    annotations=truth['annotations'];sources=train['sources']
    if truth['schema']!='graphreader.axis-ranker-train-truth-binding.v1' or len(annotations)!=64 or len(sources)!=64 or len(capture['sources'])!=64 or len(panel['sources'])!=64:raise ValueError('source inventory differs')
    if audit['audited_panels']!=96 or audit['exit_status_mismatches'] or audit['report_failures'] or audit['panel_failures'] or audit['empty_candidates'] or audit['capture_receipt_sha256']!=CAPTURE_SHA:raise ValueError('capture audit is incomplete')
    if capture['truth_read'] or capture['dev_read'] or capture['reserved_read'] or capture['optimizer_steps'] or capture['training_admitted']:raise ValueError('capture scope differs')
    ids=[r['scene_id'] for r in annotations]
    if len(set(ids))!=64 or len({r['path'] for r in annotations})!=64 or set(ids)!={r['id'] for r in sources} or set(ids)!={r['source_id'] for r in capture['sources']} or set(ids)!={r['id'] for r in panel['sources']}:raise ValueError('source/scene identities differ')
    validate_annotation_entries(annotations)
    for i,row in enumerate(annotations):
        if row['scene_id']!=sources[i]['scene_id']:raise ValueError('source order differs')
    if sum(len(row['panels']) for row in panel['sources'])!=96:raise ValueError('panel count differs')
    return annotations
def freeze():
    if ADMISSION.exists():raise ValueError('admission exists')
    with ExitStack() as stack:
        for p in (INPUT,TRUTH,TRAIN,CAPTURE,PANEL,AUDIT):lock(stack,p)
        input_doc=read(INPUT,INPUT_SHA);truth=read(TRUTH,input_doc['train_truth_binding_sha256']);train=read(TRAIN,input_doc['train_inputs_sha256']);capture=read(CAPTURE,CAPTURE_SHA);panel=read(PANEL,PANEL_SHA);audit=read(AUDIT,AUDIT_SHA)
        annotations=validate_metadata(truth,train,capture,panel,audit)
        files={p.relative_to(ROOT).as_posix():h for p,h in ((INPUT,INPUT_SHA),(TRUTH,input_doc['train_truth_binding_sha256']),(TRAIN,input_doc['train_inputs_sha256']),(CAPTURE,CAPTURE_SHA),(PANEL,PANEL_SHA),(AUDIT,AUDIT_SHA))}
        for i,row in enumerate(capture['sources']):
            if row['report_status']!='success' or row['exit_code']!=0 or row['process_status']!='completed':raise ValueError('failed exporter')
            files[(CAPTURE.parent/f'{i:03}.json').relative_to(ROOT).as_posix()]=row['report_sha256']
        for name in ('features.py','labels.py','__init__.py'):
            p=Path(__file__).with_name(name);files[p.relative_to(ROOT).as_posix()]=sha(p.read_bytes())
        p=ROOT/'ml/axis/__init__.py';files[p.relative_to(ROOT).as_posix()]=sha(p.read_bytes())
        files[Path(__file__).relative_to(ROOT).as_posix()]=sha(Path(__file__).read_bytes())
        for name,h in files.items():p=safe(ROOT/name);lock(stack,p)
        # Verify again while the no-writer handles remain held.
        for name,h in files.items():
            if sha((ROOT/name).read_bytes())!=h:raise ValueError('execution or capture bytes differ')
        doc={'schema':'graphreader.axis-ranker-train-label-admission.v2','files':files,'annotations':annotations,'source_count':64,'panel_count':96,'annotations_opened':False,'dev_read':False,'reserved_read':False,'optimizer_steps':0,'training_admitted':False,'coordinate_policy':'Four finite convex source-pixel vertices; one-to-one plot-center to encoded-crop match; detected panels only; panel-to-source translation verified.','target_policy':'Mean corner distance normalized independently by source-plot width and height, native candidate order; no features in target output.','limits':['Metadata hashes are from the preexisting train corpus binding; annotation bytes were not opened.','Unmatched, ambiguous, failed and full-image fallback sources remain in counters.','Shared generator/font limits independence; no production or model approval.']}
        raw=canonical(doc);safe(ADMISSION.parent);ADMISSION.mkdir()
        with (ADMISSION/'binding.json').open('xb') as f:f.write(raw)
        return sha(raw)
def plot_rows(annotation,scene_id,width,height):
    if not isinstance(annotation,dict) or annotation.get('scene_id')!=scene_id or annotation.get('coordinate_space')!='original_pixels' or annotation.get('canvas',{}).get('width')!=width or annotation.get('canvas',{}).get('height')!=height or not isinstance(annotation.get('plots'),list):raise ValueError('annotation scene, canvas or coordinate space differs')
    rows=[]
    for p in annotation['plots']:
        if not isinstance(p,dict) or p.get('coordinate_space')!='original_pixels':raise ValueError('plot coordinate space differs')
        poly=p.get('polygon');identifier=p.get('panel_id')
        if not isinstance(identifier,str) or not identifier or not isinstance(poly,list) or len(poly)!=4:raise ValueError('plot must contain four vertices and ID')
        for point in poly:
            if not isinstance(point,list) or len(point)!=2 or any(isinstance(v,bool) or not isinstance(v,(int,float)) or not math.isfinite(v) for v in point) or not (0<=point[0]<=width and 0<=point[1]<=height):raise ValueError('invalid source plot vertex')
        rows.append({'id':identifier,'polygon':poly})
    return rows
def crop_for(panel):
    c=panel['encoded_crop_in_source_pixels'];x,y,w,h=(c[k] for k in ('X','Y','Width','Height'))
    if any(isinstance(v,bool) or not isinstance(v,(int,float)) or not math.isfinite(v) for v in (x,y,w,h)) or min(x,y)<0 or min(w,h)<=0 or [1,0,-x,0,1,-y,0,0,1]!=panel['source_to_panel_matrix_row_major'] or [1,0,x,0,1,y,0,0,1]!=panel['panel_to_source_matrix_row_major'] or (w,h)!=(panel['panel_width'],panel['panel_height']):raise ValueError('panel crop transform differs')
    return [x,y,w,h]
def run(expected):
    if OUT.exists():raise ValueError('run exists')
    with ExitStack() as stack:
        p=ADMISSION/'binding.json';lock(stack,p);binding=read(p,expected)
        if binding['schema']!='graphreader.axis-ranker-train-label-admission.v2' or any(binding[k] is not False for k in ('annotations_opened','dev_read','reserved_read','training_admitted')) or binding['optimizer_steps']!=0:raise ValueError('label scope differs')
        for name,h in binding['files'].items():
            path=safe(ROOT/name);lock(stack,path)
            if sha(path.read_bytes())!=h:raise ValueError('bound file differs')
        # Only after all non-truth input identities are frozen and held open.
        validate_annotation_entries(binding['annotations'])
        for row in binding['annotations']:
            lock(stack,ROOT/row['path'])
        labels=importlib.import_module('ml.axis.candidate_ranker_foundation_v1.labels');features=importlib.import_module('ml.axis.candidate_ranker_foundation_v1.features')
        train=read(TRAIN,binding['files'][TRAIN.relative_to(ROOT).as_posix()])['sources'];capture=read(CAPTURE,CAPTURE_SHA);panel_doc=read(PANEL,PANEL_SHA)
        panel_by_id={r['id']:r for r in panel_doc['sources']};targets=[];summaries=[]
        for index,(a,source,row) in enumerate(zip(binding['annotations'],train,capture['sources'],strict=True)):
            if a['scene_id']!=source['scene_id'] or a['scene_id']!=row['source_id']:raise ValueError('source/scene mismatch')
            annotation=read(ROOT/a['path'],a['sha256']);result=read(CAPTURE.parent/f'{index:03}.json',row['report_sha256']);summary=result['source_summaries']
            matches=[s for s in summary if s['source_id']==a['scene_id']]
            if len(matches)!=1 or matches[0]['source_sha256']!=source['image_sha256']:raise ValueError('source image mismatch')
            plots=plot_rows(annotation,a['scene_id'],matches[0]['source_width'],matches[0]['source_height']);src=panel_by_id[a['scene_id']]
            panel_rows=[];result_panels={p['panel_id']:p for p in result['panel_results']}
            if len(result_panels)!=len(result['panel_results']) or set(result_panels)!={x['panel_id'] for x in src['panels']}:raise ValueError('panel results differ')
            retained={x['panel_id']:x for x in src['panels']}
            for pid,item in result_panels.items():
                if item['source_sha256']!=source['image_sha256']:raise ValueError('panel source differs')
                if item['panel_sha256']!=retained[pid]['sha256'] or item['source_to_panel_matrix_row_major']!=retained[pid]['source_to_panel'] or item['panel_to_source_matrix_row_major']!=retained[pid]['panel_to_source']:raise ValueError('retained panel identity differs')
                crop=crop_for(item);success=item['status']=='success'
                if crop[0]+crop[2]>matches[0]['source_width'] or crop[1]+crop[3]>matches[0]['source_height']:raise ValueError('panel crop exceeds source')
                panel_rows.append({'id':pid,'crop':crop,'exporter_success':success,'candidate_count':len(item['candidate_snapshots']) if success else None})
            association=labels.associate(src['source_status'],plots,panel_rows);summaries.append({'source_id':a['scene_id'],**association['summary']})
            by_plot={r['id']:r for r in plots}
            for match in association['matches']:
                item=result_panels[match['panel_id']];snaps=item['candidate_snapshots'];mat=item['normalized_features']['rows']
                group=features.snapshot_group(item['source_sha256'],item['panel_sha256'],item['panel_width'],item['panel_height'],snaps);errors=labels.continuous_targets(by_plot[match['plot_id']],crop_for(item),snaps)
                if len(mat)!=len(snaps) or any(s['Rank']!=i or mat[i]['rank']!=i+1 for i,s in enumerate(snaps)):raise ValueError('rank mapping differs')
                targets.append({'source_sha256':item['source_sha256'],'panel_sha256':item['panel_sha256'],'rows':[{'rank':r['rank'],'candidate_identity_sha256':r['candidate_identity_sha256'],'target_error':error} for r,error in zip(group['rows'],errors,strict=True)]})
        for name,h in binding['files'].items():
            if sha((ROOT/name).read_bytes())!=h:raise ValueError('bound file changed')
        read(p,expected);safe(OUT.parent);OUT.mkdir();target_bytes=canonical({'schema':'graphreader.axis-ranker-continuous-targets.v1','groups':targets})
        with (OUT/'targets.json').open('xb') as f:f.write(target_bytes)
        receipt={'schema':'graphreader.axis-ranker-train-label-receipt.v2','binding_sha256':expected,'targets_sha256':sha(target_bytes),'source_summaries':summaries,'train_annotations_read':True,'dev_read':False,'reserved_read':False,'optimizer_steps':0,'training_admitted':False}
        raw=canonical(receipt)
        with (OUT/'receipt.json').open('xb') as f:f.write(raw)
        return sha(raw)
if __name__=='__main__':
    if sys.argv[1:]==['freeze']:print('label_binding_sha256',freeze())
    elif len(sys.argv)==3 and sys.argv[1]=='run':print('label_receipt_sha256',run(sys.argv[2]))
    else:raise SystemExit('usage: train_labels_v2.py freeze | run <reviewed-binding-sha>')
