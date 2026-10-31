import unittest
from unittest.mock import patch
from PIL import Image
from ml.synthetic.axis_roles_dev_dataset_v1 import prepare as p

class CorpusTests(unittest.TestCase):
 def test_disjoint_recipes_cover_each_design_overlay(self):
  self.assertEqual(len({r[0] for r in p.DEV+p.RESERVED}),48)
  self.assertEqual(len({(d,o) for _,d,o in p.DEV}),16)
  self.assertEqual(len({(d,o) for _,d,o in p.RESERVED}),16)
  self.assertTrue(all(not 20261040<=s<20261280 for s,_,_ in p.DEV+p.RESERVED))
 def test_underlay_is_deterministic_and_preserves_annotation(self):
  scene=p.base._scene('ab',20270100);original=Image.new
  baseline,annotation,_=p.base._render(scene)
  untouched,plain,_=p._render(scene,'none')
  self.assertEqual(baseline.tobytes(),untouched.tobytes());self.assertEqual(annotation,plain)
  first,ann1,side1=p._render(scene,'grid_frame');second,ann2,side2=p._render(scene,'grid_frame')
  self.assertEqual(first.tobytes(),second.tobytes());self.assertNotEqual(first.tobytes(),baseline.tobytes())
  self.assertEqual(annotation,ann1);self.assertEqual(ann1,ann2);self.assertEqual(side1,side2)
  self.assertEqual(len(side1['lines']),8*len(scene['panels']))
  self.assertTrue(all('original_start' in line and 'original_end' in line for line in side1['lines']))
  self.assertIs(Image.new,original);p.base.validate_labels(scene,ann1,set())
 def test_constructor_restored_after_renderer_failure(self):
  scene=p.base._scene('ab',20270100);original=Image.new
  with patch.object(p.renderer,'render_scene',side_effect=RuntimeError('synthetic failure')):
   with self.assertRaises(RuntimeError):p._render(scene,'grid')
  self.assertIs(Image.new,original)
 def test_reserved_recipes_remain_metadata(self):
  data=p._json({'recipes':p._rows(p.RESERVED),'rendered':False,'pixels_opened':False})
  self.assertNotIn(b'.png',data);self.assertEqual(len(p._rows(p.RESERVED)),16)
if __name__=='__main__':unittest.main()
