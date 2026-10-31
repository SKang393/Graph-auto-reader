# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Fresh seeds and natural nested identities; no models or reserved pixels."""
from collections import Counter
from hashlib import sha256
from importlib.metadata import version
from io import BytesIO
import json,math,sys
from pathlib import Path
from uuid import UUID,uuid5
from ml.synthetic.axis_roles_dev_dataset_v1 import prepare as v1
from ml.synthetic.camera_document_profile_v1.schema import validate_scene

ROOT=Path(__file__).resolve().parents[3];SCHEMA='graphreader.synthetic-axis-roles-dev.v3'
DEV=tuple((20270300+i,v1.DESIGNS[i%4],v1.OVERLAYS[(i//4)%4]) for i in range(32))
RESERVED=tuple((20270400+i,v1.DESIGNS[i%4],v1.OVERLAYS[(i//4)%4]) for i in range(16))
HISTORY={'axis-roles-dev-corpus-v1-20261031':'950d5c2c229cb3dea9d7c033fa6d495f14137e2d81a2ff52d464fbad27100ff4','axis-roles-dev-corpus-v2-20261031':'5029bb489e92a1c4561d738b8e5ad96d36a13a695e3d2cbdb58201a988d737ef','synthetic-tick-corpus-v3-20261031-v1':'7bb461f712307b17987242ec358ecca043bdca963871dee306a7285268c1f2e7'}
def _sha(b):return sha256(b).hexdigest()
def _json(d):return (json.dumps(d,sort_keys=True,separators=(',',':'),ensure_ascii=False,allow_nan=False)+'\n').encode()
def _recipes(rows):return [{'seed':s,'design':d,'overlay':o,'participant_format':'full' if i<16 else 'numeric'} for i,(s,d,o) in enumerate(rows)]
def _reserved():return [{'seed':s,'design':d,'overlay':o,'participant_format':'full' if (i%4+i//4)%2==0 else 'numeric'} for i,(s,d,o) in enumerate(RESERVED)]
def _uuid_values(value):
 out=set()
 def walk(node):
  if isinstance(node,dict):
   for k,v in node.items():
    if k.endswith('_id') and isinstance(v,str):
     try:
      parsed=UUID(v)
      if str(parsed)==v:out.add(v)
     except ValueError:pass
    walk(v)
  elif isinstance(node,list):
   for v in node:walk(v)
 walk(value);return out
def _refs(scene):
 panels=scene['panels'];ids=[p['panel_id'] for p in panels];pids=set(ids)
 if len(ids)!=len(pids):raise ValueError('duplicate panels')
 for p in panels:
  phases={x['phase_id'] for x in p['phases']};series={x['series_id'] for x in p['series']};points={x['point_id']:x for x in p['points']}
  if len(phases)!=len(p['phases']) or len(series)!=len(p['series']) or len(points)!=len(p['points']):raise ValueError('duplicate panel objects')
  for x in p['points']:
   if x.get('phase_id') not in phases or x.get('series_id') not in series:raise ValueError('point reference differs')
  for e in p['connections']:
   if e['from_point_id'] not in points or e['to_point_id'] not in points or e['series_id'] not in series or points[e['from_point_id']]['series_id']!=e['series_id'] or points[e['to_point_id']]['series_id']!=e['series_id']:raise ValueError('edge reference differs')
  for e in p['legend']['entries']:
   if e['series_id'] not in series:raise ValueError('legend reference differs')
 for collection in (scene['annotations']['text_regions'],scene['annotations']['artifacts'],scene['hard_negatives']):
  if any(x['panel_id'] not in pids for x in collection):raise ValueError('annotation panel differs')
def _history():
 old_ids=set();old_images=set();old_seeds={7621,7622,7623,7624}
 for name,h in HISTORY.items():
  root=ROOT/'artifacts'/name;raw=(root/'manifest.json').read_bytes()
  if _sha(raw)!=h:raise ValueError('historical manifest differs')
  m=json.loads(raw)
  if name.startswith('synthetic-tick'):
   for c in m['source_cases']:
    old_seeds.add(c['seed']);data=(root/c['scene']).read_bytes()
    if _sha(data)!=c['scene_sha256']:raise ValueError('historical scene differs')
    old_ids.update(_uuid_values(json.loads(data)));old_images.add(c['source_image_sha256'])
  else:
   for c in m['dev_cases']:
    old_seeds.add(c['seed']);data=(root/f"scenes/{c['index']}.json").read_bytes()
    if _sha(data)!=m['artifact_sha256'][f"scenes/{c['index']}.json"]:raise ValueError('historical scene differs')
    old_ids.update(_uuid_values(json.loads(data)));old_images.add(c['image_sha256'])
 if {s for s,_,_ in DEV+RESERVED}&old_seeds:raise ValueError('seed overlap')
 return old_ids,old_images
def _scene(recipe):
 scene=v1.base._scene(recipe['design'],recipe['seed']);v1.base._check_scene(scene,recipe['design'],recipe['seed']);base_id=scene['scene_id'];scene['scene_id']=str(uuid5(UUID(base_id),f"axis-roles-development-v3:{recipe['participant_format']}:{recipe['overlay']}"));expected={}
 for panel in scene['panels']:
  number=10+((recipe['seed']*13+panel['index']*7)%90);text=f'Participant {number:02d}' if recipe['participant_format']=='full' else f'{number:02d}';rows=[x for x in scene['annotations']['text_regions'] if x['panel_id']==panel['panel_id'] and x['role']=='participant']
  if len(rows)!=1:raise ValueError('participant contract differs')
  panel['participant']=text;rows[0]['text']=text;expected[panel['panel_id']]=text
 validate_scene(scene);_refs(scene);return scene,expected
def _material(recipe,seen):
 scene,expected=_scene(recipe);image,ann,side=v1._render(scene,recipe['overlay']);roles,_=v1.base.validate_labels(scene,ann,seen)
 if ann['font']['sha256']!='b85c38ecea8a7cfb39c24e395a4007474fa5a4fc864f6ee33309eb4948d232d5':raise ValueError('font differs')
 participants=[x for panel in ann['panels'] for x in panel['texts'] if x['role']=='participant']
 if len(participants)!=len(expected) or {x['panel_id']:x['text'] for x in participants}!=expected or any(not x['visible'] for x in participants):raise ValueError('participant differs')
 for item in participants:
  x,y,w,h=item['rendered_pixel_box'];l=max(0,math.floor(x));t=max(0,math.floor(y));r=min(image.width,math.ceil(x+w));b=min(image.height,math.ceil(y+h))
  if l>=r or t>=b or image.convert('RGB').crop((l,t,r,b)).convert('L').getextrema()[0]>=210:raise ValueError('participant has no dark pixels')
 matrix=ann['transforms'][0]['forward_matrix_3x3'];plots={x['panel_id']:x['polygon'] for x in ann['plots']}
 if set(plots)!={p['panel_id'] for p in scene['panels']}:raise ValueError('plot panel mismatch')
 for panel in scene['panels']:
  x,y,w,h=panel['plot_box'];expected_corners=[v1.base._project(matrix,p) for p in [[x,y],[x+w,y],[x+w,y+h],[x,y+h]]];actual=plots[panel['panel_id']]
  if len(actual)!=4 or any(not math.isfinite(z) for p in actual for z in p) or max(abs(u-v) for a,b in zip(expected_corners,actual,strict=True) for u,v in zip(a,b,strict=True))>0.00002:raise ValueError('transformed plot differs')
 out=BytesIO();image.save(out,format='PNG');return scene,ann,side,out.getvalue(),roles,expected
def generate(output):
 output=Path(output).resolve()
 if not output.is_relative_to((ROOT/'artifacts').resolve()) or output.exists():raise ValueError('fresh project artifact required')
 old_ids,old_images=_history();snapshot=v1.base._snapshot();output.mkdir(parents=True)
 for name in ('images','scenes','annotations','overlays'):(output/name).mkdir()
 reserved=_json({'schema':'graphreader.synthetic-axis-role-reserved-recipes.v3','recipes':_reserved(),'rendered':False,'pixels_opened':False});(output/'reserved-recipes.json').write_bytes(reserved)
 hashes={'reserved-recipes.json':_sha(reserved)};cases=[];seen_text=set();ids=set();images=set();roles=Counter();formats=Counter();labels=set()
 for i,recipe in enumerate(_recipes(DEV)):
  scene,ann,side,png,case_roles,texts=_material(recipe,seen_text);local=_uuid_values(scene);h=_sha(png)
  if local&old_ids or local&ids or h in old_images or h in images:raise ValueError('source identity collision')
  ids.update(local);images.add(h);roles.update(case_roles);formats[recipe['participant_format']]+=len(texts);labels.update(texts.values())
  artifacts={f'images/{i}.png':png,f'scenes/{i}.json':_json(scene),f'annotations/{i}.json':_json(ann),f'overlays/{i}.json':_json(side)}
  for path,data in artifacts.items():
   with (output/path).open('xb') as f:f.write(data)
   hashes[path]=_sha(data)
  cases.append({'index':i,**recipe,'scene_id':scene['scene_id'],'image_sha256':h,'annotation_sha256':hashes[f'annotations/{i}.json'],'overlay_sha256':hashes[f'overlays/{i}.json']})
 if v1.base._snapshot(snapshot['commit'])!=snapshot:raise ValueError('generator changed')
 m={'schema':SCHEMA,'dev_recipes':_recipes(DEV),'reserved_recipes_sha256':_sha(reserved),'dev_cases':cases,'artifact_sha256':hashes,'source_snapshot':snapshot,'historical_manifests':HISTORY,'historical_nested_uuid_overlap':0,'historical_image_overlap':0,'nested_uuid_count':len(ids),'v1_wrapper_sha256':_sha(Path(v1.__file__).read_bytes()),'base_wrapper_sha256':_sha(Path(v1.base.__file__).read_bytes()),'wrapper_sha256':_sha(Path(__file__).read_bytes()),'participant_formats':dict(formats),'distinct_participant_texts':len(labels),'visible_text_roles':dict(sorted(roles.items())),'python_version':sys.version.split()[0],'pillow_version':version('Pillow'),'jsonschema_version':version('jsonschema'),'reserved_pixels_accessed':False,'sealed_reads':0,'private_reads':0,'optimizer_steps':0,'training_approved':False,'limits':['Reserved group is recipe-only and has not been rendered.','Development and reserved groups share generator, renderer and bundled font; seed/UUID disjointness is not family independence.','Historical independence beyond the listed manifests and four known camera seeds is unproven.','Dark-pixel presence in rendered text bounds is not glyph readability or OCR accuracy.','Cross-host byte reproducibility is unproven; grid/frame underlays are synthetic additions behind graph ink.']}
 raw=_json(m)
 with (output/'manifest.json').open('xb') as f:f.write(raw)
 return _sha(raw)
def verify(output,expected):
 output=Path(output).resolve(strict=True)
 if not output.is_relative_to((ROOT/'artifacts').resolve()):raise ValueError('outside project')
 raw=(output/'manifest.json').read_bytes()
 if _sha(raw)!=expected:raise ValueError('manifest differs')
 m=json.loads(raw)
 if m['schema']!=SCHEMA or m['dev_recipes']!=_recipes(DEV) or m['reserved_pixels_accessed'] or m['optimizer_steps'] or m['sealed_reads'] or m['private_reads'] or m['wrapper_sha256']!=_sha(Path(__file__).read_bytes()) or m['v1_wrapper_sha256']!=_sha(Path(v1.__file__).read_bytes()) or m['base_wrapper_sha256']!=_sha(Path(v1.base.__file__).read_bytes()) or m['historical_manifests']!=HISTORY:raise ValueError('scope/source differs')
 if m['source_snapshot']!=v1.base._snapshot(m['source_snapshot']['commit']):raise ValueError('snapshot differs')
 if {p.relative_to(output).as_posix() for p in output.rglob('*') if p.is_file()}!=set(m['artifact_sha256'])|{'manifest.json'}:raise ValueError('inventory differs')
 for path,h in m['artifact_sha256'].items():
  if _sha((output/path).read_bytes())!=h:raise ValueError('artifact differs')
 reserved=json.loads((output/'reserved-recipes.json').read_bytes())
 if reserved['recipes']!=_reserved() or reserved['rendered'] or reserved['pixels_opened']:raise ValueError('reserved differs')
 old_ids,old_images=_history();seen=set();ids=set();images=set();roles=Counter();formats=Counter();labels=set()
 for i,recipe in enumerate(_recipes(DEV)):
  scene,ann,side,png,case_roles,texts=_material(recipe,seen);local=_uuid_values(scene);h=_sha(png)
  if local&old_ids or local&ids or h in old_images or h in images:raise ValueError('identity collision')
  ids.update(local);images.add(h);roles.update(case_roles);formats[recipe['participant_format']]+=len(texts);labels.update(texts.values())
  artifacts={f'images/{i}.png':png,f'scenes/{i}.json':_json(scene),f'annotations/{i}.json':_json(ann),f'overlays/{i}.json':_json(side)}
  if any(_sha(data)!=m['artifact_sha256'][p] for p,data in artifacts.items()):raise ValueError('regenerated bytes differ')
  if m['dev_cases'][i]!={'index':i,**recipe,'scene_id':scene['scene_id'],'image_sha256':h,'annotation_sha256':_sha(artifacts[f'annotations/{i}.json']),'overlay_sha256':_sha(artifacts[f'overlays/{i}.json'])}:raise ValueError('case metadata differs')
 if len(ids)!=m['nested_uuid_count'] or dict(formats)!=m['participant_formats'] or len(labels)!=m['distinct_participant_texts'] or dict(sorted(roles.items()))!=m['visible_text_roles']:raise ValueError('summary differs')
 return {'schema':SCHEMA,'manifest_sha256':expected,'regenerated_cases':len(DEV),'historical_nested_uuid_overlap':0,'reserved_pixels_accessed':False}
if __name__=='__main__':
 if len(sys.argv)==3 and sys.argv[1]=='generate':print('manifest_sha256',generate(sys.argv[2]))
 elif len(sys.argv)==4 and sys.argv[1]=='verify':print(json.dumps(verify(sys.argv[2],sys.argv[3]),sort_keys=True))
 else:raise SystemExit('usage: python -m ml.synthetic.axis_roles_dev_dataset_v3.prepare generate <new-artifact-dir> | verify <dir> <manifest-sha256>')
