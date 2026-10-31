# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Bounded train-source production panelization; never reads annotations."""
from contextlib import ExitStack
from hashlib import sha256
import importlib.util,json,sys
from pathlib import Path
from ml.synthetic.axis_roles_training_catalog_v1 import catalog

ROOT=Path(__file__).resolve().parents[3];A=ROOT/'artifacts/goal22-runs/axis-ranker-panelization-admission-v1';OUT=ROOT/'artifacts/goal22-runs/axis-ranker-panelization-run-v1'
def _sha(b):return sha256(b).hexdigest()
def _read(p,h):
 b=p.read_bytes()
 if _sha(b)!=h:raise ValueError('bound bytes differ')
 return json.loads(b)
def run(expected):
 if OUT.exists():raise ValueError('run exists')
 with ExitStack() as stack:
  binding_path=A/'binding.json';catalog._lock_read(stack,binding_path);binding=_read(binding_path,expected)
  if binding['schema']!='graphreader.axis-ranker-panelization-binding.v1' or binding['annotations_read'] or binding['reserved_read'] or binding['optimizer_steps'] or binding['training_admitted']:raise ValueError('scope differs')
  for name,h in binding['files'].items():
   p=ROOT/name;catalog._lock_read(stack,p)
   if _sha(p.read_bytes())!=h:raise ValueError('execution file differs')
  for item in binding['sources']:
   p=ROOT/item['path'];catalog._lock_read(stack,p)
   if _sha(p.read_bytes())!=item['sha256']:raise ValueError('source differs')
  if len(binding['sources'])!=64 or len({x['id'] for x in binding['sources']})!=64:raise ValueError('source inventory differs')
  job_path=ROOT/'artifacts/goal22-tools/axis-roles-dev-measure-v2/job.py';spec=importlib.util.spec_from_file_location('bound_panel_job',job_path);job=importlib.util.module_from_spec(spec);spec.loader.exec_module(job)
  catalog.v4._assert_no_reparse_ancestors(OUT.parent,ROOT);OUT.mkdir();rows=[]
  for item in binding['sources']:
   folder=OUT/item['id'];folder.mkdir();arg=folder/'source.json';arg.write_text(json.dumps({'sources':[item]},sort_keys=True),encoding='utf-8');target=folder/'panelization'
   code,status=job.launch([ROOT/'artifacts/goal22-runs/axis-roles-panelization-build-v2/Panelization.exe',arg,target],folder/'panelization.log',120)
   row={'id':item['id'],'source_sha256':item['sha256'],'exit_code':code,'process_status':status,'source_status':'failed','panels':[],'warnings':[]}
   if code==0:
    p=target/'panelization.json';doc=json.loads(p.read_bytes());sources=doc['sources']
    if doc['truth_read'] or len(sources)!=1 or sources[0]['id']!=item['id'] or sources[0]['source_sha256']!=item['sha256'] or sources[0]['status'] not in ('detected','no_panel_fallback','failed') or sources[0]['panel_count']!=len(sources[0]['panels']):raise ValueError('panelization output differs')
    row.update({'source_status':sources[0]['status'],'warnings':sources[0]['warnings'],'panelization_sha256':_sha(p.read_bytes()),'panels':sources[0]['panels']})
    for panel in row['panels']:
     if Path(panel['path']).name!=panel['path'] or _sha((target/panel['path']).read_bytes())!=panel['sha256']:raise ValueError('panel SHA differs')
   rows.append(row);print('panelized',len(rows),'of 64',flush=True)
  for name,h in binding['files'].items():
   if _sha((ROOT/name).read_bytes())!=h:raise ValueError('execution file changed')
  if _sha(binding_path.read_bytes())!=expected:raise ValueError('binding changed')
  doc={'schema':'graphreader.axis-ranker-training-panelization-receipt.v1','binding_sha256':expected,'sources':rows,'truth_read':False,'dev_read':False,'reserved_read':False,'optimizer_steps':0,'training_admitted':False,'limits':['Production import panelization only; no candidate export or labels.','Full-image no-panel fallbacks and failures are explicitly retained.','A separate reviewed exporter binding and source-only capture are required before truth joining.']}
  raw=(json.dumps(doc,sort_keys=True,indent=2)+'\n').encode()
  with (OUT/'receipt.json').open('xb') as f:f.write(raw)
  return _sha(raw)
if __name__=='__main__':
 if len(sys.argv)!=2:raise SystemExit('usage: panelize_train <reviewed-binding-sha>')
 print('receipt_sha256',run(sys.argv[1]))
