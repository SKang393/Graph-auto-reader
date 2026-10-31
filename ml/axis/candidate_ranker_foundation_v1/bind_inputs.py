# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Bind train-source inputs and dev identities before any panelization.

No annotation content, development pixels, reserved recipes or model is read.
The later exporter must have its own exact execution binding and admission.
"""
from contextlib import ExitStack
from hashlib import sha256
import json
from pathlib import Path
from ml.synthetic.axis_roles_training_catalog_v1 import catalog

ROOT=Path(__file__).resolve().parents[3];OUT=ROOT/'artifacts/goal22-runs/axis-ranker-input-admission-v1';TRAIN=ROOT/'artifacts/axis-roles-training-corpus-v1-20261031';DEV=ROOT/'artifacts/axis-roles-dev-corpus-v3-20261031'
TRAIN_SHA='765762ac77daeea9d4326bbeeca8e3e7dc25b9c094708a4b7533be8bb733e8c9';DEV_SHA='c3c735a38dac5aa893331958ba09319e7fcfac534f8122738d8d07db37f507b1';SOURCE_SHA='11c1b21e45ca88b6849b04e9a6c9337c5f832fa760069654e3da907e40a8d7ff'
def _sha(b):return sha256(b).hexdigest()
def _json(v):return (json.dumps(v,sort_keys=True,indent=2,allow_nan=False)+'\n').encode()
def _load(path,h):
 b=path.read_bytes()
 if _sha(b)!=h:raise ValueError('source manifest differs')
 return json.loads(b)
def assert_disjoint(train,dev):
 for name in ('seed','scene_id','image_sha256'):
  a=[r[name] for r in train];b=[r[name] for r in dev]
  if len(a)!=len(set(a)) or len(b)!=len(set(b)) or set(a)&set(b):raise ValueError('duplicate or overlapping '+name)
def bind():
 catalog.v4._assert_no_reparse_ancestors(OUT.parent,ROOT)
 if OUT.exists():raise ValueError('input admission exists')
 with ExitStack() as stack:
  source_binding,c=catalog._locked_sources(stack,SOURCE_SHA)
  files=[TRAIN/'manifest.json',DEV/'manifest.json',Path(__file__),Path(__file__).with_name('features.py'),Path(__file__).with_name('labels.py')]
  for p in files:catalog._lock_read(stack,p)
  train=_load(TRAIN/'manifest.json',TRAIN_SHA);dev=_load(DEV/'manifest.json',DEV_SHA)
  if train['training_admitted'] or train['reserved_read'] or train['optimizer_steps'] or len(train['cases'])!=64 or dev['reserved_pixels_accessed'] or len(dev['dev_cases'])!=32:raise ValueError('source scope differs')
  train_rows=[];truth=[]
  for i,case in enumerate(train['cases']):
   if case['index']!=i or case['recipe']!=c['recipes'][i] or case['recipe']['split_role']!='train':raise ValueError('training recipe differs')
   rel=f'images/{i}.png';path=TRAIN/rel;catalog._lock_read(stack,path);h=train['artifact_sha256'][rel]
   if _sha(path.read_bytes())!=h or case['artifact_hashes']['image_png']!=h:raise ValueError('training source image differs')
   train_rows.append({'id':case['scene_id'],'scene_id':case['scene_id'],'seed':case['recipe']['seed'],'image_sha256':h,'path':path.relative_to(ROOT).as_posix()})
   annotation=f'annotations/{i}.json';truth.append({'scene_id':case['scene_id'],'path':(TRAIN/annotation).relative_to(ROOT).as_posix(),'sha256':train['artifact_sha256'][annotation]})
  dev_rows=[{'scene_id':x['scene_id'],'seed':x['seed'],'image_sha256':x['image_sha256']} for x in dev['dev_cases']]
  assert_disjoint(train_rows,dev_rows)
  if {x['seed'] for x in train_rows}&set(range(20270400,20270416)):raise ValueError('reserved seed overlap')
  ib=_json({'schema':'graphreader.axis-ranker-train-source-input.v1','sources':train_rows});tb=_json({'schema':'graphreader.axis-ranker-train-truth-binding.v1','annotations':truth});db=_json({'schema':'graphreader.axis-ranker-dev-identity.v1','sources':dev_rows})
  doc={'schema':'graphreader.axis-ranker-input-binding.v1','train_manifest_sha256':TRAIN_SHA,'dev_manifest_sha256':DEV_SHA,'renderer_source_binding_sha256':SOURCE_SHA,'catalog_sha256':catalog.PROTOCOL_SHA256,'train_inputs_sha256':_sha(ib),'train_truth_binding_sha256':_sha(tb),'dev_identities_sha256':_sha(db),'foundation_sources':{p.relative_to(ROOT).as_posix():_sha(p.read_bytes()) for p in files[2:]},'train_source_count':64,'dev_source_count':32,'reserved_pixels_opened':False,'dev_pixels_opened':False,'annotations_opened':False,'panelization_started':False,'optimizer_steps':0,'training_admitted':False,'limits':['This is an input/split binding only; production panelizer/exporter and execution source still require a separate reviewed binding.','Annotation hashes come from the reviewed train corpus manifest; contents are deferred until source-only inference is frozen.','Train/dev source hashes and seeds are disjoint, but share synthetic generator/font; reserved IDs/pixels are unverified because those recipes remain unopened.']}
  raw=_json(doc);OUT.mkdir(parents=True)
  for name,data in [('train-inputs.json',ib),('train-truth-binding.json',tb),('dev-identities.json',db),('input-binding.json',raw)]:
   with (OUT/name).open('xb') as f:f.write(data)
  catalog._verify_locked_sources(source_binding,SOURCE_SHA)
  return _sha(raw)
if __name__=='__main__':print('input_binding_sha256',bind())
