import unittest
from ml.axis.candidate_ranker_foundation_v1.bind_inputs import assert_disjoint
class InputSplitTests(unittest.TestCase):
 def test_distinct_sources_pass(self):
  assert_disjoint([{'seed':1,'scene_id':'a','image_sha256':'x'}],[{'seed':2,'scene_id':'b','image_sha256':'y'}])
 def test_any_cross_split_overlap_fails(self):
  a=[{'seed':1,'scene_id':'a','image_sha256':'x'}]
  for field,value in [('seed',1),('scene_id','a'),('image_sha256','x')]:
   b={'seed':2,'scene_id':'b','image_sha256':'y'};b[field]=value
   with self.assertRaises(ValueError):assert_disjoint(a,[b])
 def test_within_split_duplicates_fail(self):
  a=[{'seed':1,'scene_id':'a','image_sha256':'x'},{'seed':1,'scene_id':'b','image_sha256':'y'}]
  with self.assertRaises(ValueError):assert_disjoint(a,[{'seed':2,'scene_id':'c','image_sha256':'z'}])
if __name__=='__main__':unittest.main()
