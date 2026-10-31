import unittest
import json,subprocess,sys
import tempfile
from concurrent.futures import ThreadPoolExecutor
from collections import Counter
from copy import deepcopy
from pathlib import Path
from unittest.mock import patch
from ml.synthetic.axis_roles_training_catalog_v1 import catalog
SOURCE_BINDING_SHA='11c1b21e45ca88b6849b04e9a6c9337c5f832fa760069654e3da907e40a8d7ff'

class CatalogTests(unittest.TestCase):
 def test_fixed_recipes_are_train_only_balanced_and_disjoint(self):
  rows=catalog._expected();self.assertEqual(len(rows),64)
  self.assertEqual(len({r['seed'] for r in rows}),64)
  self.assertTrue(all(r['split_role']=='train' and 20270600<=r['seed']<=20270663 for r in rows))
  cells=Counter((r['design'],r['overlay'],r['participant_format']) for r in rows)
  self.assertEqual(len(cells),32);self.assertEqual(set(cells.values()),{2})
  self.assertFalse({r['seed'] for r in rows}&set(range(20270300,20270516)))
 def test_wrong_role_or_hash_and_tampered_recipe_rejected(self):
  rows=catalog._expected();base={'schema':'graphreader.synthetic-axis-role-train-worker-request.v1','catalog_sha256':catalog.PROTOCOL_SHA256,'source_binding_sha256':SOURCE_BINDING_SHA,'split_role':'train','recipe':rows[0]};self.assertEqual(catalog.validate_request(base,{'recipes':rows}),rows[0])
  for field,value in [('split_role','validation'),('catalog_sha256','0'*64),('schema','wrong')]:
   request=deepcopy(base);request[field]=value
   with self.assertRaises(ValueError):catalog.validate_request(request,{'recipes':rows})
  request=deepcopy(base);request['recipe']['seed']=20270400
  with self.assertRaises(ValueError):catalog.validate_request(request,{'recipes':rows})
 def test_reserved_role_is_rejected(self):
  rows=catalog._expected();request={'schema':'graphreader.synthetic-axis-role-train-worker-request.v1','catalog_sha256':catalog.PROTOCOL_SHA256,'source_binding_sha256':SOURCE_BINDING_SHA,'split_role':'reserved','recipe':rows[0]}
  with self.assertRaises(ValueError):catalog.validate_request(request,{'recipes':rows})
 def test_worker_rejects_wrong_role_or_catalog_before_render(self):
  recipe=catalog._expected()[0]
  for role,digest,source_sha in [('reserved',catalog.PROTOCOL_SHA256,SOURCE_BINDING_SHA),('train','0'*64,SOURCE_BINDING_SHA),('train',catalog.PROTOCOL_SHA256,'0'*64),('train',catalog.PROTOCOL_SHA256,None)]:
   request={'schema':'graphreader.synthetic-axis-role-train-worker-request.v1','catalog_sha256':digest,'source_binding_sha256':source_sha,'split_role':role,'recipe':recipe}
   done=subprocess.run([sys.executable,'-m','ml.synthetic.axis_roles_training_catalog_v1.worker'],input=json.dumps(request).encode(),capture_output=True,cwd=str(catalog.ROOT),env=catalog.v4._clean_child_environment(),timeout=30,check=False)
   self.assertEqual(done.returncode,2);self.assertEqual(done.stdout,b'');self.assertIn(b'ValueError',done.stderr)
 def test_unreviewed_source_binding_rejected_before_render(self):
  with self.assertRaises(ValueError):catalog.run_children([catalog._expected()[0]],'0'*64)
 def test_bound_worker_tampering_rejected_before_render(self):
  fake=json.loads(catalog.SOURCE_BINDING.read_bytes());fake['files']['ml/synthetic/axis_roles_training_catalog_v1/worker.py']='0'*64
  raw=catalog.v4._canonical_json(fake)
  with tempfile.TemporaryDirectory(dir=catalog.ROOT/'artifacts/test-temp') as folder:
   receipt=Path(folder)/'fake-binding.json';receipt.write_bytes(raw)
   with patch.object(catalog,'SOURCE_BINDING',receipt),patch.object(catalog,'_one',side_effect=AssertionError('must not render')):
    with self.assertRaises(ValueError):catalog.run_children([catalog._expected()[0]],catalog._sha(raw))
 def test_child_serial_parallel_and_parent_pil_are_isolated(self):
  from PIL import Image
  protocol=catalog.load_catalog();recipes=protocol['recipes'][:2];original=Image.new
  serial=catalog.run_children(recipes,SOURCE_BINDING_SHA,max_workers=1)
  with ThreadPoolExecutor(max_workers=1) as pool:
   future=pool.submit(catalog.run_children,recipes,SOURCE_BINDING_SHA,2)
   for _ in range(20):self.assertEqual(Image.new('RGB',(3,4),'white').getpixel((0,0)),(255,255,255))
   parallel=future.result(timeout=120)
  self.assertIs(Image.new,original);self.assertEqual([r['hashes'] for r in serial],[r['hashes'] for r in parallel])
  self.assertEqual(len({r['child_pid'] for r in parallel}),2);self.assertTrue(all(r['split_role']=='train' for r in parallel))
if __name__=='__main__':unittest.main()
