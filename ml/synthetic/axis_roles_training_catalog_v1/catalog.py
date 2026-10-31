# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Strict recipe admission and isolated rendering; no parent PIL import."""
from concurrent.futures import ThreadPoolExecutor
from contextlib import ExitStack
from hashlib import sha256
import ctypes,json,re,subprocess,sys
from ctypes import wintypes
from pathlib import Path,PurePosixPath
from ml.synthetic.axis_roles_dev_dataset_v4 import prepare as v4

ROOT=Path(__file__).resolve().parents[3];PROTOCOL=Path(__file__).with_name('protocol-v2.json');PROTOCOL_SHA256='1eaa322ca5706c393fd3d83820723994d50892734ce3e7331331e0fbb85e52ff'
SOURCE_BINDING=Path(__file__).with_name('source-binding-v2.json')
DESIGNS=('ab','aba','abab','multiple_baseline');OVERLAYS=('none','grid','frame','grid_frame')
def _sha(b):return sha256(b).hexdigest()
def _expected():return [{'index':i,'seed':20270600+i,'design':DESIGNS[i%4],'overlay':OVERLAYS[(i//4)%4],'participant_format':'full' if (i//16)%2==0 else 'numeric','split_role':'train'} for i in range(64)]
def load_catalog():
 raw=PROTOCOL.read_bytes()
 if _sha(raw)!=PROTOCOL_SHA256:raise ValueError('training catalog hash differs')
 p=json.loads(raw)
 if p.get('schema')!='graphreader.synthetic-axis-role-train-catalog.v1' or p.get('scope')!='synthetic_training_input_development_only' or p.get('recipes')!=_expected() or p.get('training_admitted') is not False or p.get('optimizer_steps')!=0 or p.get('reserved_rendered') is not False:raise ValueError('training catalog scope differs')
 if p.get('v4_protocol_sha256')!=v4.PROTOCOL_SHA256:raise ValueError('V4 protocol identity differs')
 v4_protocol=v4.load_protocol()
 if v4._runtime_source_bindings(v4_protocol)!=p.get('v4_runtime_source_bindings'):raise ValueError('V4/upstream source bundle differs')
 if p.get('font_provenance')!=v4_protocol['font_provenance']:raise ValueError('font provenance differs')
 protected=p.get('protected_seed_ranges',[])+p.get('historical_seed_ranges',[])
 if any(lo<=r['seed']<=hi for r in p['recipes'] for lo,hi in protected):raise ValueError('protected seed overlaps training catalog')
 return p
def validate_request(request,catalog):
 if not isinstance(request,dict) or set(request)!={'schema','catalog_sha256','source_binding_sha256','split_role','recipe'} or request.get('schema')!='graphreader.synthetic-axis-role-train-worker-request.v1' or request.get('catalog_sha256')!=PROTOCOL_SHA256 or request.get('split_role')!='train' or not isinstance(request.get('source_binding_sha256'),str) or not re.fullmatch(r'[0-9a-f]{64}',request['source_binding_sha256']):raise ValueError('request hash, source binding, role, or schema differs')
 recipe=request.get('recipe')
 if not isinstance(recipe,dict):raise ValueError('invalid recipe')
 i=recipe.get('index')
 if isinstance(i,bool) or not isinstance(i,int) or not 0<=i<64 or recipe!=catalog['recipes'][i] or recipe['split_role']!='train':raise ValueError('recipe differs from train-only catalog')
 return recipe
def _one(recipe,catalog,v4_protocol,source_binding_sha256):
 request={'schema':'graphreader.synthetic-axis-role-train-worker-request.v1','catalog_sha256':PROTOCOL_SHA256,'source_binding_sha256':source_binding_sha256,'split_role':'train','recipe':recipe};data=v4._canonical_json(request)
 if len(data)>v4.MAX_REQUEST_BYTES:raise ValueError('request too large')
 temp=ROOT/'artifacts/test-temp';v4._assert_no_reparse_ancestors(temp,ROOT)
 if not temp.is_dir():raise ValueError('project-local temporary directory is missing')
 env=v4._clean_child_environment();env['TEMP']=str(temp);env['TMP']=str(temp)
 try:
  completed=subprocess.run([sys.executable,'-m','ml.synthetic.axis_roles_training_catalog_v1.worker'],input=data,stdout=subprocess.PIPE,stderr=subprocess.PIPE,cwd=str(ROOT),env=env,timeout=v4.CHILD_TIMEOUT_SECONDS,check=False)
 except subprocess.TimeoutExpired as exc:raise RuntimeError('isolated train-catalog render timed out') from exc
 if completed.returncode!=0:raise RuntimeError('isolated train-catalog render failed: '+completed.stderr.decode('utf-8',errors='replace')[-1000:])
 if len(completed.stdout)>v4.MAX_RESPONSE_BYTES:raise ValueError('worker response too large')
 payload=json.loads(completed.stdout.decode('utf-8'))
 if payload.get('schema')!='graphreader.synthetic-axis-role-train-worker-result.v1' or payload.get('catalog_sha256')!=PROTOCOL_SHA256 or payload.get('source_binding_sha256')!=source_binding_sha256 or payload.get('split_role')!='train':raise ValueError('worker result scope differs')
 # Reuse V4's complete parent-side geometry, reference, font and hash validation.
 adapted={k:v for k,v in payload.items() if k not in ('catalog_sha256','source_binding_sha256','split_role')};adapted['schema']='graphreader.synthetic-axis-roles-dev-v4.worker-result.v1'
 result=v4._validate_worker_payload(adapted,recipe,v4_protocol);result['split_role']='train';result['catalog_sha256']=PROTOCOL_SHA256
 return result
def _lock_read(stack,path):
 if sys.platform!='win32':raise ValueError('Windows read-only source locking is required')
 v4._assert_no_reparse_ancestors(path,ROOT)
 kernel=ctypes.WinDLL('kernel32',use_last_error=True);kernel.CreateFileW.argtypes=[wintypes.LPCWSTR,wintypes.DWORD,wintypes.DWORD,ctypes.c_void_p,wintypes.DWORD,wintypes.DWORD,wintypes.HANDLE];kernel.CreateFileW.restype=wintypes.HANDLE;kernel.CloseHandle.argtypes=[wintypes.HANDLE];kernel.CloseHandle.restype=wintypes.BOOL
 handle=kernel.CreateFileW(str(path),0x80000000,1,None,3,0x80,None)
 if handle==ctypes.c_void_p(-1).value:raise ctypes.WinError(ctypes.get_last_error())
 stack.callback(kernel.CloseHandle,handle)
def _locked_sources(stack,expected):
 if not isinstance(expected,str) or not re.fullmatch(r'[0-9a-f]{64}',expected):raise ValueError('reviewed source-binding SHA required')
 _lock_read(stack,SOURCE_BINDING);raw=SOURCE_BINDING.read_bytes()
 if _sha(raw)!=expected:raise ValueError('source binding SHA differs')
 binding=json.loads(raw)
 if binding.get('schema')!='graphreader.synthetic-axis-training-source-binding.v1' or binding.get('catalog_sha256')!=PROTOCOL_SHA256 or not isinstance(binding.get('files'),dict):raise ValueError('source binding schema differs')
 files=binding['files'];required={str(p.relative_to(ROOT).as_posix()) for p in [Path(__file__),Path(__file__).with_name('__init__.py'),Path(__file__).with_name('worker.py'),Path(__file__).with_name('bind_sources.py'),PROTOCOL]}
 if not required<=set(files):raise ValueError('source binding omits training guards')
 for name,digest in files.items():
  rel=PurePosixPath(name)
  if rel.is_absolute() or '..' in rel.parts or '\\' in name or not re.fullmatch(r'[0-9a-f]{64}',digest):raise ValueError('invalid bound source path or SHA')
  path=ROOT.joinpath(*rel.parts);_lock_read(stack,path)
  if _sha(path.read_bytes())!=digest:raise ValueError('bound source differs')
 catalog=load_catalog()
 if not set(catalog['v4_runtime_source_bindings'])<=set(files):raise ValueError('source binding omits V4 dependencies')
 return binding,catalog
def _verify_locked_sources(binding,expected):
 if _sha(SOURCE_BINDING.read_bytes())!=expected:raise ValueError('source binding changed during rendering')
 for name,digest in binding['files'].items():
  if _sha(ROOT.joinpath(*PurePosixPath(name).parts).read_bytes())!=digest:raise ValueError('bound source changed during rendering')
def run_children(recipes,expected_source_binding_sha256,max_workers=1):
 if isinstance(max_workers,bool) or not isinstance(max_workers,int) or not 1<=max_workers<=2:raise ValueError('worker count must be one or two')
 with ExitStack() as stack:
  binding,catalog=_locked_sources(stack,expected_source_binding_sha256);v4_protocol=v4.load_protocol();requested=list(recipes)
  for recipe in requested:validate_request({'schema':'graphreader.synthetic-axis-role-train-worker-request.v1','catalog_sha256':PROTOCOL_SHA256,'source_binding_sha256':expected_source_binding_sha256,'split_role':'train','recipe':recipe},catalog)
  if len({r['index'] for r in requested})!=len(requested):raise ValueError('duplicate recipe')
  with ThreadPoolExecutor(max_workers=max_workers) as pool:results=list(pool.map(lambda r:_one(r,catalog,v4_protocol,expected_source_binding_sha256),requested))
  _verify_locked_sources(binding,expected_source_binding_sha256)
  if load_catalog()!=catalog:raise ValueError('catalog changed during rendering')
  return sorted(results,key=lambda x:x['recipe']['index'])
