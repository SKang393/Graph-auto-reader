import subprocess,sys,unittest
from ml.synthetic.axis_roles_training_materializer_v1 import materialize as m
class MaterializerTests(unittest.TestCase):
 def test_uuid_walk_deduplicates_refs_and_ignores_non_uuid_labels(self):
  s={'scene_id':'12345678-1234-5678-1234-567812345678','panels':[{'panel_id':'22345678-1234-5678-1234-567812345678'}],'refs':[{'panel_id':'22345678-1234-5678-1234-567812345678','style_id':'circle'}]}
  self.assertEqual(len(m._uuids(s)),2)
 def test_parent_import_does_not_load_pil(self):
  code="import sys;from ml.synthetic.axis_roles_training_materializer_v1 import materialize;assert 'PIL' not in sys.modules"
  done=subprocess.run([sys.executable,'-c',code],capture_output=True,cwd=str(m.ROOT),env=m.catalog.v4._clean_child_environment(),timeout=30,check=False)
  self.assertEqual(done.returncode,0,done.stderr.decode(errors='replace'))
 def test_recipe_catalog_has_no_reserved_role(self):
  self.assertTrue(all(x['split_role']=='train' for x in m.catalog.load_catalog()['recipes']))
if __name__=='__main__':unittest.main()
