# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Materialize only the reviewed train-role catalog in isolated children."""
from contextlib import ExitStack
from hashlib import sha256
import json,sys
from pathlib import Path
from uuid import UUID
from ml.synthetic.axis_roles_training_catalog_v1 import catalog

ROOT=Path(__file__).resolve().parents[3];A=ROOT/'artifacts/goal22-runs/axis-train-source-admission-v1';OUT=ROOT/'artifacts/axis-roles-training-corpus-v1-20261031'
SOURCE_SHA='11c1b21e45ca88b6849b04e9a6c9337c5f832fa760069654e3da907e40a8d7ff'
HISTORY={'axis-roles-dev-corpus-v1-20261031':'950d5c2c229cb3dea9d7c033fa6d495f14137e2d81a2ff52d464fbad27100ff4','axis-roles-dev-corpus-v2-20261031':'5029bb489e92a1c4561d738b8e5ad96d36a13a695e3d2cbdb58201a988d737ef','axis-roles-dev-corpus-v3-20261031':'c3c735a38dac5aa893331958ba09319e7fcfac534f8122738d8d07db37f507b1','synthetic-tick-corpus-v3-20261031-v1':'7bb461f712307b17987242ec358ecca043bdca963871dee306a7285268c1f2e7'}
def _sha(b):return sha256(b).hexdigest()
def _json(o):return catalog.v4._canonical_json(o)
def _read(path,expected):
 catalog.v4._assert_no_reparse_ancestors(path,ROOT)
 b=path.read_bytes()
 if _sha(b)!=expected:raise ValueError('bound bytes differ')
 return json.loads(b)
def _uuids(value):
 found=set()
 def walk(o):
  if isinstance(o,dict):
   for k,v in o.items():
    if k.endswith('_id') and isinstance(v,str):
     try:
      if str(UUID(v))==v:found.add(v)
     except ValueError:pass
    walk(v)
  elif isinstance(o,list):
   for v in o:walk(v)
 walk(value);return found
def _historical():
 ids=set();images=set()
 for name,h in HISTORY.items():
  root=ROOT/'artifacts'/name;m=_read(root/'manifest.json',h)
  if name.startswith('synthetic-tick'):
   for c in m['source_cases']:
    scene=_read(root/c['scene'],c['scene_sha256']);ids.update(_uuids(scene));images.add(c['source_image_sha256'])
  else:
   for c in m['dev_cases']:
    path=f"scenes/{c['index']}.json";scene=_read(root/path,m['artifact_sha256'][path]);ids.update(_uuids(scene));images.add(c['image_sha256'])
 return ids,images
def _admission(expected,stack):
 p=A/'binding.json';catalog._lock_read(stack,p);m=_read(p,expected)
 if m['schema']!='graphreader.axis-training-source-admission.v1' or m['catalog_sha256']!=catalog.PROTOCOL_SHA256 or m['source_binding_sha256']!=SOURCE_SHA or m['training_admitted'] or m['optimizer_steps'] or m['reserved_read']:raise ValueError('admission scope differs')
 for path,h in m['materializer_sources'].items():
  p=ROOT/path;catalog._lock_read(stack,p)
  if _sha(p.read_bytes())!=h:raise ValueError('materializer source differs')
 c=catalog.load_catalog()
 if m['recipes']!=c['recipes'] or len(c['recipes'])!=64:raise ValueError('recipe inventory differs')
 return m,c
def generate(expected):
 if OUT.exists():raise ValueError('output exists')
 with ExitStack() as stack:
  admission,c=_admission(expected,stack);old_ids,old_images=_historical();catalog.v4._assert_no_reparse_ancestors(OUT.parent,ROOT);OUT.mkdir()
  for name in ('images','scenes','annotations','overlays'):(OUT/name).mkdir()
  hashes={};cases=[];ids=set();images=set()
  for start in range(0,64,2):
   for result in catalog.run_children(c['recipes'][start:start+2],SOURCE_SHA,max_workers=2):
    recipe=result['recipe'];i=recipe['index'];scene=result['scene'];ann=result['annotation'];side=result['overlay'];png=result['image_png'];local=_uuids(scene);h=_sha(png)
    if local&old_ids or local&ids or h in old_images or h in images:raise ValueError('historical or within-corpus source collision')
    ids.update(local);images.add(h);payloads={f'images/{i}.png':png,f'scenes/{i}.json':_json(scene),f'annotations/{i}.json':_json(ann),f'overlays/{i}.json':_json(side)}
    for name,data in payloads.items():
     with (OUT/name).open('xb') as f:f.write(data)
     hashes[name]=_sha(data)
    cases.append({'index':i,'recipe':recipe,'recipe_id':result['recipe_id'],'scene_id':scene['scene_id'],'artifact_hashes':result['hashes']})
   print('materialized',len(cases),'of 64',flush=True)
  if len(cases)!=64 or [x['index'] for x in cases]!=list(range(64)):raise ValueError('materialization incomplete')
  doc={'schema':'graphreader.axis-training-source-corpus.v1','admission_sha256':expected,'catalog_sha256':catalog.PROTOCOL_SHA256,'source_binding_sha256':SOURCE_SHA,'materializer_sha256':_sha(Path(__file__).read_bytes()),'historical_manifests':HISTORY,'cases':cases,'artifact_sha256':hashes,'nested_uuid_count':len(ids),'checked_historical_uuid_overlap':0,'checked_historical_image_overlap':0,'reserved_read':False,'private_reads':0,'sealed_reads':0,'optimizer_steps':0,'training_admitted':False,'limits':['Generated scenes are synthetic and share generator/font with development and recipe-only reserved groups.','Historical checks cover only the four listed development corpus manifests; V4 nonmaterialized scenes are excluded by seeds, not checked pixels.','No automatic recognition or model training acceptance.']}
  raw=_json(doc)
  with (OUT/'manifest.json').open('xb') as f:f.write(raw)
  if _sha((A/'binding.json').read_bytes())!=expected:raise ValueError('admission changed')
  return _sha(raw)
def verify(expected_admission,expected_manifest):
 with ExitStack() as stack:
  catalog.v4._assert_no_reparse_ancestors(OUT,ROOT)
  admission,c=_admission(expected_admission,stack);m=_read(OUT/'manifest.json',expected_manifest)
  if m['admission_sha256']!=expected_admission or m['catalog_sha256']!=catalog.PROTOCOL_SHA256 or m['source_binding_sha256']!=SOURCE_SHA or m['training_admitted'] or m['reserved_read'] or len(m['cases'])!=64:raise ValueError('manifest scope differs')
  expected_files=set(m['artifact_sha256'])|{'manifest.json'}
  if catalog.v4._safe_inventory(OUT)!=expected_files:raise ValueError('output inventory differs')
  old_ids,old_images=_historical();ids=set();images=set();count=0
  for start in range(0,64,2):
   for result in catalog.run_children(c['recipes'][start:start+2],SOURCE_SHA,max_workers=2):
    i=result['recipe']['index'];scene=result['scene'];png=result['image_png'];local=_uuids(scene);h=_sha(png)
    if local&old_ids or local&ids or h in old_images or h in images:raise ValueError('identity collision')
    ids.update(local);images.add(h);payloads={f'images/{i}.png':png,f'scenes/{i}.json':_json(scene),f'annotations/{i}.json':_json(result['annotation']),f'overlays/{i}.json':_json(result['overlay'])}
    for name,data in payloads.items():
     if _sha(data)!=m['artifact_sha256'][name] or _sha((OUT/name).read_bytes())!=m['artifact_sha256'][name]:raise ValueError('regenerated artifact differs')
    if m['cases'][i]!={'index':i,'recipe':result['recipe'],'recipe_id':result['recipe_id'],'scene_id':scene['scene_id'],'artifact_hashes':result['hashes']}:raise ValueError('case metadata differs')
    count+=1
   print('verified',count,'of 64',flush=True)
  if len(ids)!=m['nested_uuid_count']:raise ValueError('UUID count differs')
  return {'manifest_sha256':expected_manifest,'regenerated':count,'reserved_read':False,'training_admitted':False}
if __name__=='__main__':
 if len(sys.argv)==3 and sys.argv[1]=='generate':print('manifest_sha256',generate(sys.argv[2]))
 elif len(sys.argv)==4 and sys.argv[1]=='verify':print(json.dumps(verify(sys.argv[2],sys.argv[3]),sort_keys=True))
 else:raise SystemExit('usage: generate <reviewed-admission-sha> | verify <reviewed-admission-sha> <manifest-sha>')
