# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Bind exact train PNGs and existing production importer before panelization."""
from contextlib import ExitStack
from hashlib import sha256
import json,struct
from pathlib import Path
from ml.synthetic.axis_roles_training_catalog_v1 import catalog
from . import panelize_train as runner

ROOT=Path(__file__).resolve().parents[3];A=ROOT/'artifacts/goal22-runs/axis-ranker-input-admission-v1';OUT=ROOT/'artifacts/goal22-runs/axis-ranker-panelization-admission-v1';INPUT_SHA='147cf08b51ea23640d846857cfb1339ccc892e9fdc9567b3f099d375c4e1a320'
def _sha(b):return sha256(b).hexdigest()
def _read(p,h):
 b=p.read_bytes()
 if _sha(b)!=h:raise ValueError('bound bytes differ')
 return json.loads(b)
def png_dimensions(b):
 if len(b)<24 or b[:8]!=b'\x89PNG\r\n\x1a\n' or b[12:16]!=b'IHDR' or struct.unpack('>I',b[8:12])[0]!=13:raise ValueError('invalid PNG header')
 w,h=struct.unpack('>II',b[16:24])
 if not 0<w<=8192 or not 0<h<=8192 or w*h>32000000:raise ValueError('image exceeds fixed resource bounds')
 return w,h
def bind():
 catalog.v4._assert_no_reparse_ancestors(OUT.parent,ROOT)
 if OUT.exists():raise ValueError('admission exists')
 with ExitStack() as stack:
  catalog._lock_read(stack,A/'input-binding.json');prior=_read(A/'input-binding.json',INPUT_SHA);catalog._lock_read(stack,A/'train-inputs.json');inputs=_read(A/'train-inputs.json',prior['train_inputs_sha256'])['sources']
  existing=ROOT/'artifacts/goal22-runs/axis-roles-dev-measure-admission-v2/binding.json';catalog._lock_read(stack,existing);panel_binding=_read(existing,'a32b43b05c74ea2279f2a7cd90e53024c5981f356dbf8735adbc8d1653168203')
  executable_prefix='artifacts/goal22-runs/axis-roles-panelization-build-v2/';files={k:v for k,v in panel_binding['runtime_files'].items() if k.startswith(executable_prefix)}
  dependencies=[Path(__file__),Path(runner.__file__),ROOT/'artifacts/goal22-tools/axis-roles-dev-measure-v2/job.py',ROOT/'artifacts/goal22-tools/axis-roles-production-panelization-v2/Program.cs',ROOT/'artifacts/goal22-tools/axis-roles-production-panelization-v2/Panelization.csproj',ROOT/'src/GraphReader.App/Integration/Workflow/ProductionWorkflowImportStage.cs',ROOT/'src/GraphReader.App/Integration/Workflow/ProductionRasterPanelizer.cs',ROOT/'src/GraphReader.App/Integration/Workflow/RasterPanelSourceProvenance.cs',ROOT/'src/GraphReader.Imaging/ImageImportService.cs']
  for p in dependencies:
   name=p.relative_to(ROOT).as_posix();digest=_sha(p.read_bytes())
   if p not in (Path(__file__),Path(runner.__file__)) and panel_binding['files'].get(name)!=digest:raise ValueError('reviewed panelizer source differs')
   files[name]=digest
  for name,h in files.items():
   path=ROOT/name;catalog._lock_read(stack,path)
   if _sha(path.read_bytes())!=h:raise ValueError('execution dependency differs')
  sources=[]
  for item in inputs:
   p=ROOT/item['path'];catalog._lock_read(stack,p);raw=p.read_bytes()
   if _sha(raw)!=item['image_sha256']:raise ValueError('training image differs')
   w,h=png_dimensions(raw);sources.append({'id':item['id'],'path':item['path'],'sha256':item['image_sha256'],'width':w,'height':h})
  if len(sources)!=64 or len({x['id'] for x in sources})!=64:raise ValueError('source count differs')
  doc={'schema':'graphreader.axis-ranker-panelization-binding.v1','input_binding_sha256':INPUT_SHA,'source_count':64,'sources':sources,'files':files,'bounds':{'max_width':8192,'max_height':8192,'max_pixels':32000000,'child_memory_bytes':2147483648,'timeout_seconds':120,'parallel_children':1},'annotations_read':False,'dev_pixels_read':False,'reserved_read':False,'optimizer_steps':0,'training_admitted':False,'limits':['Binds panelization only; a separate reviewed observer-export binding is still required.','No-panel fallbacks and failed imports are recorded distinctly.','Dev and train share generator/font; no independent external acceptance.','Operating system and installed .NET shared framework are outside runtime file hashes.']}
  raw=(json.dumps(doc,sort_keys=True,indent=2)+'\n').encode();OUT.mkdir(parents=True)
  with (OUT/'binding.json').open('xb') as f:f.write(raw)
  return _sha(raw)
if __name__=='__main__':print('panelization_binding_sha256',bind())
