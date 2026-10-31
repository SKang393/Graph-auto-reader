# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Generate a development-only, project-local axis-role corpus, without models.

The reserved recipe group is written as metadata only: no scene or pixels are
generated for it. A short-lived single-thread underlay hooks the existing PIL
canvas constructor once, before the unchanged camera renderer paints content.
"""
from collections import Counter
from hashlib import sha256
from importlib.metadata import version
from io import BytesIO
import json
import math
from pathlib import Path
import re
import sys
from unittest.mock import patch

from PIL import Image, ImageDraw
from ml.synthetic.camera_document_dev_dataset_v1 import prepare as base
from ml.synthetic.camera_document_profile_v1 import renderer

ROOT=Path(__file__).resolve().parents[3]
SCHEMA='graphreader.synthetic-axis-roles-dev.v1'
DESIGNS=('ab','aba','abab','multiple_baseline')
OVERLAYS=('none','grid','frame','grid_frame')
DEV=tuple((20270100+i,DESIGNS[i%4],OVERLAYS[(i//4)%4]) for i in range(32))
RESERVED=tuple((20270200+i,DESIGNS[i%4],OVERLAYS[(i//4)%4]) for i in range(16))

def _sha(data):return sha256(data).hexdigest()
def _json(data):return (json.dumps(data,sort_keys=True,separators=(',',':'),ensure_ascii=False,allow_nan=False)+'\n').encode()
def _rows(recipes):return [{'seed':seed,'design':design,'overlay':overlay} for seed,design,overlay in recipes]

def _segments(scene,variant):
 if variant not in OVERLAYS:raise ValueError('unsupported overlay')
 lines=[]
 for panel in scene['panels']:
  x,y,w,h=panel['plot_box'];right=x+w;bottom=y+h;pid=panel['panel_id']
  if variant in ('grid','grid_frame'):
   for i in (1,2):
    lines.append({'panel_id':pid,'kind':'grid_vertical','start':[x+w*i/3,y],'end':[x+w*i/3,bottom]})
    lines.append({'panel_id':pid,'kind':'grid_horizontal','start':[x,y+h*i/3],'end':[right,y+h*i/3]})
  if variant in ('frame','grid_frame'):
   for a,b in [([x,y],[right,y]),([right,y],[right,bottom]),([right,bottom],[x,bottom]),([x,bottom],[x,y])]:
    lines.append({'panel_id':pid,'kind':'frame','start':a,'end':b})
 return lines

def _render(scene,variant):
 """Paint under graph ink once; restore PIL globally even if rendering fails."""
 segments=_segments(scene,variant);width=scene['canvas']['width'];height=scene['canvas']['height'];original=Image.new;calls=[]
 def factory(mode,size,*args,**kwargs):
  image=original(mode,size,*args,**kwargs)
  if not calls:
   if mode!='RGB' or tuple(size)!=(width,height):raise ValueError('unexpected renderer canvas constructor')
   calls.append(True);draw=ImageDraw.Draw(image)
   for line in segments:
    draw.line((tuple(line['start']),tuple(line['end'])),fill=(180,180,180) if line['kind'].startswith('grid') else (96,96,96),width=1)
  return image
 with patch.object(renderer.Image,'new',side_effect=factory):image,annotation,mask=renderer.render_scene(scene)
 if len(calls)!=1 or Image.new is not original:raise ValueError('canvas hook failed to restore')
 if image.size!=(width,height) or mask.size!=image.size or mask.mode!='L':raise ValueError('renderer dimensions differ')
 transforms=annotation.get('transforms',[])
 if len(transforms)!=1 or transforms[0].get('kind')!='camera_document':raise ValueError('unexpected transform')
 matrix=transforms[0]['forward_matrix_3x3'];annotated=[]
 for line in segments:
  item=dict(line);item['original_start']=base._project(matrix,line['start']);item['original_end']=base._project(matrix,line['end']);annotated.append(item)
 side={'schema':'graphreader.synthetic-axis-role-underlay.v1','variant':variant,'lines':annotated,
       'line_width_clean_pixels':1,'grid_rgb':[180,180,180],'frame_rgb':[96,96,96],
       'note':'Strokes are painted on clean background before graph ink and the camera transform; output endpoints are transformed continuous coordinates.'}
 return image,annotation,side

def _baseline():
 old=ROOT/'artifacts/synthetic-tick-corpus-v3-20261031-v1/manifest.json';data=old.read_bytes()
 if _sha(data)!='7bb461f712307b17987242ec358ecca043bdca963871dee306a7285268c1f2e7':raise ValueError('historical V3 inventory differs')
 v=json.loads(data);historical={c['seed'] for c in v['source_cases']}|{7621,7622,7623,7624};seeds={x[0] for x in DEV+RESERVED}
 if len(seeds)!=48 or seeds&historical:raise ValueError('seed overlap')
 return {'historical_v3_manifest_sha256':_sha(data),'historical_camera_seeds':[7621,7622,7623,7624],'historical_scene_ids':{c['scene_id'] for c in v['source_cases']},'historical_image_sha256':{c['source_image_sha256'] for c in v['source_cases']}}

def _material(seed,design,overlay,seen):
 scene=base._scene(design,seed);base._check_scene(scene,design,seed);image,annotation,side=_render(scene,overlay)
 roles,_=base.validate_labels(scene,annotation,seen)
 texts=[t for p in annotation['panels'] for t in p['texts']]
 if roles.get('participant',0)<1 or roles.get('x_tick',0)<1 or not any(t.get('visible') and t.get('role')=='participant' and re.search(r'\d',t.get('text','')) for t in texts):raise ValueError('required numeric participant or tick roles absent')
 if annotation['font']['sha256']!='b85c38ecea8a7cfb39c24e395a4007474fa5a4fc864f6ee33309eb4948d232d5':raise ValueError('font differs')
 out=BytesIO();image.save(out,format='PNG');return scene,annotation,side,out.getvalue(),roles

def generate(output):
 output=Path(output).resolve()
 if not output.is_relative_to((ROOT/'artifacts').resolve()) or output.exists():raise ValueError('use a fresh project artifacts path')
 baseline=_baseline();snapshot=base._snapshot();output.mkdir(parents=True)
 for name in ('images','scenes','annotations','overlays'): (output/name).mkdir()
 reserved=_json({'schema':'graphreader.synthetic-axis-role-reserved-recipes.v1','recipes':_rows(RESERVED),'rendered':False,'pixels_opened':False})
 (output/'reserved-recipes.json').write_bytes(reserved)
 hashes={'reserved-recipes.json':_sha(reserved)};cases=[];seen=set();scene_ids=set();images=set();totals=Counter()
 for i,(seed,design,variant) in enumerate(DEV):
  scene,annotation,side,png,roles=_material(seed,design,variant,seen);sid=scene['scene_id'];h=_sha(png)
  if sid in scene_ids or h in images or sid in baseline['historical_scene_ids'] or h in baseline['historical_image_sha256']:raise ValueError('duplicate historical or new source')
  scene_ids.add(sid);images.add(h);totals.update(roles)
  artifacts={f'images/{i}.png':png,f'scenes/{i}.json':_json(scene),f'annotations/{i}.json':_json(annotation),f'overlays/{i}.json':_json(side)}
  for path,data in artifacts.items():
   with (output/path).open('xb') as f:f.write(data)
   hashes[path]=_sha(data)
  cases.append({'index':i,'seed':seed,'design':design,'overlay':variant,'scene_id':sid,'image_sha256':h,'annotation_sha256':hashes[f'annotations/{i}.json'],'overlay_sha256':hashes[f'overlays/{i}.json']})
 if base._snapshot(snapshot['commit'])!=snapshot:raise ValueError('generator source changed')
 manifest={'schema':SCHEMA,'dev_recipes':_rows(DEV),'reserved_recipes_sha256':_sha(reserved),'dev_cases':cases,'artifact_sha256':hashes,'source_snapshot':snapshot,'wrapper_sha256':_sha(Path(__file__).read_bytes()),'base_wrapper_sha256':_sha(Path(base.__file__).read_bytes()),'historical_v3_manifest_sha256':baseline['historical_v3_manifest_sha256'],'visible_text_roles':dict(sorted(totals.items())),'python_version':sys.version.split()[0],'pillow_version':version('Pillow'),'jsonschema_version':version('jsonschema'),'reserved_pixels_accessed':False,'sealed_reads':0,'private_reads':0,'optimizer_steps':0,'training_approved':False,'limits':['Reserved group is recipe-only and has not been rendered.','Groups share generator, renderer, bundled font and distribution; only seeds/source identities are disjoint.','Historical independence beyond checked V3 and four camera seeds is unproven.','Cross-host byte reproducibility is unproven.','Grid/frame overlays are synthetic additions behind graph ink; they are not the generator native test family.']}
 raw=_json(manifest)
 with (output/'manifest.json').open('xb') as f:f.write(raw)
 return _sha(raw)

def verify(output,expected):
 output=Path(output).resolve(strict=True)
 if not output.is_relative_to((ROOT/'artifacts').resolve()):raise ValueError('output outside project')
 data=(output/'manifest.json').read_bytes()
 if _sha(data)!=expected:raise ValueError('manifest differs')
 m=json.loads(data)
 if m['schema']!=SCHEMA or m['dev_recipes']!=_rows(DEV) or m['reserved_pixels_accessed'] or m['optimizer_steps'] or m['sealed_reads'] or m['private_reads'] or m['wrapper_sha256']!=_sha(Path(__file__).read_bytes()) or m['base_wrapper_sha256']!=_sha(Path(base.__file__).read_bytes()):raise ValueError('scope or source differs')
 if m['source_snapshot']!=base._snapshot(m['source_snapshot']['commit']):raise ValueError('snapshot differs')
 inventory={p.relative_to(output).as_posix() for p in output.rglob('*') if p.is_file()}
 if inventory!=set(m['artifact_sha256'])|{'manifest.json'}:raise ValueError('inventory differs')
 for path,h in m['artifact_sha256'].items():
  if _sha((output/path).read_bytes())!=h:raise ValueError('artifact differs')
 reserved=json.loads((output/'reserved-recipes.json').read_bytes())
 if reserved['recipes']!=_rows(RESERVED) or reserved['rendered'] or reserved['pixels_opened']:raise ValueError('reserved group differs')
 seen=set();roles=Counter();seen_images=set();seen_scenes=set()
 for i,(seed,design,variant) in enumerate(DEV):
  scene,annotation,side,png,case_roles=_material(seed,design,variant,seen);roles.update(case_roles)
  expected_files={f'images/{i}.png':png,f'scenes/{i}.json':_json(scene),f'annotations/{i}.json':_json(annotation),f'overlays/{i}.json':_json(side)}
  if any(_sha(b)!=m['artifact_sha256'][p] for p,b in expected_files.items()):raise ValueError('regenerated bytes differ')
  if scene['scene_id'] in seen_scenes or _sha(png) in seen_images:raise ValueError('duplicate source')
  seen_scenes.add(scene['scene_id']);seen_images.add(_sha(png))
 if dict(sorted(roles.items()))!=m['visible_text_roles']:raise ValueError('role summary differs')
 return {'schema':SCHEMA,'manifest_sha256':expected,'regenerated_cases':len(DEV),'reserved_pixels_accessed':False}

if __name__=='__main__':
 if len(sys.argv)==3 and sys.argv[1]=='generate':print('manifest_sha256',generate(sys.argv[2]))
 elif len(sys.argv)==4 and sys.argv[1]=='verify':print(json.dumps(verify(sys.argv[2],sys.argv[3]),sort_keys=True))
 else:raise SystemExit('usage: python -m ml.synthetic.axis_roles_dev_dataset_v1.prepare generate <new-artifact-dir> | verify <dir> <manifest-sha256>')
