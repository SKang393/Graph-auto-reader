import unittest
from collections import Counter
from copy import deepcopy
from ml.synthetic.axis_roles_dev_dataset_v3 import prepare as p

class CorpusV3Tests(unittest.TestCase):
 def test_fixed_groups_balanced_and_seed_disjoint(self):
  dev=p._recipes(p.DEV);reserved=p._reserved()
  self.assertEqual(len({x['seed'] for x in dev+reserved}),48)
  self.assertEqual(len({(x['design'],x['overlay'],x['participant_format']) for x in dev}),32)
  self.assertEqual(Counter(x['participant_format'] for x in reserved),{'full':8,'numeric':8})
  self.assertFalse({x['seed'] for x in dev+reserved}&{x[0] for x in p.v1.DEV+p.v1.RESERVED})
 def test_nested_references_reject_corruption(self):
  recipe=p._recipes(p.DEV)[0];scene,_=p._scene(recipe);p._refs(scene)
  broken=deepcopy(scene);broken['panels'][0]['connections'][0]['from_point_id']='00000000-0000-0000-0000-000000000000'
  with self.assertRaises(ValueError):p._refs(broken)
  broken=deepcopy(scene);broken['annotations']['text_regions'][0]['panel_id']='00000000-0000-0000-0000-000000000000'
  with self.assertRaises(ValueError):p._refs(broken)
 def test_separate_seeds_have_disjoint_local_uuids(self):
  a,_=p._scene(p._recipes(p.DEV)[0]);b,_=p._scene(p._recipes(p.DEV)[1]);self.assertFalse(p._uuid_values(a)&p._uuid_values(b))
 def test_material_checks_rendered_labels_and_plots(self):
  for recipe in [p._recipes(p.DEV)[0],p._recipes(p.DEV)[16]]:
   scene,ann,side,png,roles,labels=p._material(recipe,set());self.assertEqual(len(ann['plots']),len(scene['panels']));self.assertEqual(roles['participant'],len(labels));self.assertTrue(png.startswith(b'\x89PNG'))
 def test_reserved_metadata_has_no_image(self):
  self.assertNotIn(b'.png',p._json({'recipes':p._reserved(),'rendered':False,'pixels_opened':False}))
if __name__=='__main__':unittest.main()
