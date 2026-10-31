# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""One admitted train-only recipe per fresh single-thread child; no training."""
import base64,importlib.metadata,json,os,sys,threading
from contextlib import ExitStack
from .catalog import PROTOCOL_SHA256,_locked_sources,_verify_locked_sources,validate_request
from ml.synthetic.axis_roles_dev_dataset_v4 import prepare as v4
def main():
 raw=sys.stdin.buffer.read(v4.MAX_REQUEST_BYTES+1)
 if len(raw)>v4.MAX_REQUEST_BYTES:raise ValueError('request too large')
 request=json.loads(raw.decode('utf-8'))
 if not isinstance(request,dict):raise ValueError('invalid request')
 source_sha=request.get('source_binding_sha256')
 with ExitStack() as stack:
  binding,catalog=_locked_sources(stack,source_sha);recipe=validate_request(request,catalog)
  if threading.active_count()!=1:raise RuntimeError('render child is not single-threaded')
  from PIL import Image
  original_new=Image.new
  from ml.synthetic.axis_roles_dev_dataset_v3 import prepare as v3
  scene,annotation,overlay,png,_,_=v3._material(recipe,set());v3._refs(scene)
  if Image.new is not original_new or threading.active_count()!=1:raise RuntimeError('render child did not restore isolation')
  sb=v4._canonical_json(scene);ab=v4._canonical_json(annotation);ob=v4._canonical_json(overlay)
  result={'schema':'graphreader.synthetic-axis-role-train-worker-result.v1','catalog_sha256':PROTOCOL_SHA256,'source_binding_sha256':source_sha,'split_role':'train','recipe_id':v4._sha(v4._canonical_json(recipe)),'recipe':recipe,'scene':scene,'annotation':annotation,'overlay':overlay,'png_base64':base64.b64encode(png).decode('ascii'),'hashes':{'scene':v4._sha(sb),'annotation':v4._sha(ab),'overlay':v4._sha(ob),'image_png':v4._sha(png)},'child_pid':os.getpid(),'child_pil_constructor_restored':True,'runtime':{'python':sys.version.split()[0],'pillow':importlib.metadata.version('Pillow'),'jsonschema':importlib.metadata.version('jsonschema')}}
  _verify_locked_sources(binding,source_sha)
 sys.stdout.buffer.write(v4._canonical_json(result));return 0
if __name__=='__main__':
 try:raise SystemExit(main())
 except Exception as exc:sys.stderr.write(type(exc).__name__+': '+str(exc)+'\n');raise SystemExit(2)
