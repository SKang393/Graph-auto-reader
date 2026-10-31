# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Standalone, bound train-only panelization; no project imports before locks."""
from contextlib import ExitStack
from hashlib import sha256
import ctypes, importlib.util, json, os, sys
from ctypes import wintypes
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
ADMISSION = ROOT / 'artifacts/goal22-runs/axis-ranker-panelization-admission-v2'
OUTPUT = ROOT / 'artifacts/goal22-runs/axis-ranker-panelization-run-v2'
PRIOR = ROOT / 'artifacts/goal22-runs/axis-ranker-panelization-admission-v1/binding.json'
PRIOR_SHA = 'f74572dec5d0415cac35148cb0199c5440c86bedf24ce3de821a2eaac3903ecc'
SELF = Path(__file__).resolve()
def digest(data): return sha256(data).hexdigest()
def safe(path):
    path = Path(os.path.abspath(path))
    try: parts = path.relative_to(ROOT).parts
    except ValueError: raise ValueError('path escapes project')
    current = ROOT
    for component in ('', *parts):
        if component: current = current / component
        try: attributes = current.lstat().st_file_attributes
        except FileNotFoundError: continue
        if attributes & 0x400: raise ValueError('reparse path')
    return path
def lock(stack, path):
    if sys.platform != 'win32': raise ValueError('Windows no-writer locks required')
    path = safe(path)
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.CreateFileW.argtypes = [wintypes.LPCWSTR,wintypes.DWORD,wintypes.DWORD,ctypes.c_void_p,wintypes.DWORD,wintypes.DWORD,wintypes.HANDLE]
    kernel.CreateFileW.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = kernel.CreateFileW(str(path),0x80000000,1,None,3,0x80,None)
    if handle == ctypes.c_void_p(-1).value: raise ctypes.WinError(ctypes.get_last_error())
    stack.callback(kernel.CloseHandle,handle)
def checked(path, expected):
    raw = path.read_bytes()
    if digest(raw) != expected: raise ValueError('bound bytes differ')
    return raw
def freeze():
    if ADMISSION.exists(): raise ValueError('admission exists')
    with ExitStack() as stack:
        lock(stack, PRIOR); previous = json.loads(checked(PRIOR, PRIOR_SHA))
        for key in ('annotations_read','dev_pixels_read','reserved_read','training_admitted'):
            if previous[key] is not False: raise ValueError('scope differs')
        if previous['optimizer_steps'] != 0: raise ValueError('scope differs')
        files = dict(previous['files'])
        files.pop('ml/axis/candidate_ranker_foundation_v1/panelize_train.py')
        files.pop('ml/axis/candidate_ranker_foundation_v1/bind_panelization.py')
        files[SELF.relative_to(ROOT).as_posix()] = digest(SELF.read_bytes())
        for name, expected in files.items():
            path = safe(ROOT / name); lock(stack, path); checked(path, expected)
        for row in previous['sources']:
            path = safe(ROOT / row['path']); lock(stack, path); checked(path, row['sha256'])
        doc = dict(previous, schema='graphreader.axis-ranker-panelization-binding.v2', prior_binding_sha256=PRIOR_SHA, files=files)
        raw = (json.dumps(doc,sort_keys=True,indent=2)+'\n').encode()
        safe(ADMISSION.parent); ADMISSION.mkdir()
        with (ADMISSION/'binding.json').open('xb') as stream: stream.write(raw)
        return digest(raw)
def run(expected):
    if OUTPUT.exists(): raise ValueError('run exists')
    with ExitStack() as stack:
        path = ADMISSION/'binding.json'; lock(stack,path); binding = json.loads(checked(path,expected))
        if binding['schema'] != 'graphreader.axis-ranker-panelization-binding.v2': raise ValueError('binding schema differs')
        for key in ('annotations_read','dev_pixels_read','reserved_read','training_admitted'):
            if binding[key] is not False: raise ValueError('scope differs')
        if binding['optimizer_steps'] != 0: raise ValueError('scope differs')
        for name, h in binding['files'].items():
            p = safe(ROOT/name); lock(stack,p); checked(p,h)
        for item in binding['sources']:
            p = safe(ROOT/item['path']); lock(stack,p); checked(p,item['sha256'])
        if len(binding['sources']) != 64 or len({x['id'] for x in binding['sources']}) != 64: raise ValueError('source inventory differs')
        job_path=ROOT/'artifacts/goal22-tools/axis-roles-dev-measure-v2/job.py'
        spec=importlib.util.spec_from_file_location('bound_panel_job',job_path);job=importlib.util.module_from_spec(spec);spec.loader.exec_module(job)
        safe(OUTPUT.parent);OUTPUT.mkdir();rows=[]
        for item in binding['sources']:
            folder=OUTPUT/item['id'];safe(folder);folder.mkdir();arg=folder/'source.json';arg.write_text(json.dumps({'sources':[item]},sort_keys=True),encoding='utf-8');target=folder/'panelization'
            code,status=job.launch([ROOT/'artifacts/goal22-runs/axis-roles-panelization-build-v2/Panelization.exe',arg,target],folder/'panelization.log',120)
            row={'id':item['id'],'source_sha256':item['sha256'],'exit_code':code,'process_status':status,'source_status':'failed','panels':[],'warnings':[]}
            if code == 0:
                p=target/'panelization.json';raw=p.read_bytes();doc=json.loads(raw);sources=doc['sources']
                if doc['truth_read'] or len(sources)!=1 or sources[0]['id']!=item['id'] or sources[0]['source_sha256']!=item['sha256'] or sources[0]['status'] not in ('detected','no_panel_fallback','failed') or sources[0]['panel_count']!=len(sources[0]['panels']):raise ValueError('panelization output differs')
                row.update({'source_status':sources[0]['status'],'warnings':sources[0]['warnings'],'panelization_sha256':digest(raw),'panels':sources[0]['panels']})
                for panel in row['panels']:
                    if Path(panel['path']).name!=panel['path'] or digest((target/panel['path']).read_bytes())!=panel['sha256']:raise ValueError('panel SHA differs')
            rows.append(row);print('panelized',len(rows),'of 64',flush=True)
        for name,h in binding['files'].items(): checked(ROOT/name,h)
        checked(path,expected)
        doc={'schema':'graphreader.axis-ranker-training-panelization-receipt.v2','binding_sha256':expected,'sources':rows,'truth_read':False,'dev_read':False,'reserved_read':False,'optimizer_steps':0,'training_admitted':False,'limits':['Production import panelization only; no candidate export or labels.','Full-image fallback and failures are retained.','Separate reviewed exporter binding and source-only capture required before truth joining.','Python standard library, interpreter, OS and .NET shared framework are outside runtime file hashes.']}
        raw=(json.dumps(doc,sort_keys=True,indent=2)+'\n').encode()
        with (OUTPUT/'receipt.json').open('xb') as stream:stream.write(raw)
        return digest(raw)
if __name__ == '__main__':
    if sys.argv[1:] == ['freeze']: print('panelization_binding_sha256',freeze())
    elif len(sys.argv)==3 and sys.argv[1]=='run': print('receipt_sha256',run(sys.argv[2]))
    else: raise SystemExit('usage: panelize_train_v2.py freeze | run <reviewed-binding-sha>')
