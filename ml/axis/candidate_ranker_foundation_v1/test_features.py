import math,unittest
from copy import deepcopy
from ml.axis.candidate_ranker_foundation_v1.features import FEATURE_NAMES,MAX_CANDIDATES,snapshot_group
def pair(score=.9):
 return {'Score':score,'Intersection':{'X':10,'Y':90},'BottomRight':{'X':90,'Y':90},'TopRight':{'X':90,'Y':10},'TopLeft':{'X':10,'Y':10},'XSpan':80,'YSpan':80,'XCoverageFraction':1,'YCoverageFraction':.5,'XRootMeanSquareError':2,'YRootMeanSquareError':1,'XSupportingCandidateIds':['x'],'YSupportingCandidateIds':['y']}
class FeatureTests(unittest.TestCase):
 def group(self,rows):return snapshot_group('a'*64,'b'*64,100,100,rows)
 def test_finite_normalization_and_order_preserved(self):
  g=self.group([pair(),pair(.8)]);self.assertFalse(g['runtime_parity_verified']);self.assertEqual(g['feature_names'],FEATURE_NAMES);self.assertEqual(len(g['rows'][0]['features']),17)
  self.assertEqual(g['rows'][0]['features'][:3],[.9,.1,.9]);self.assertEqual([x['rank'] for x in g['rows']],[1,2]);self.assertNotEqual(g['rows'][0]['candidate_identity_sha256'],g['rows'][1]['candidate_identity_sha256'])
 def test_nonfinite_or_unsorted_rejected(self):
  p=pair();p['XSpan']=math.nan
  with self.assertRaises(ValueError):self.group([p])
  with self.assertRaises(ValueError):self.group([pair(.8),pair(.9)])
 def test_duplicate_support_and_overflow_rejected(self):
  p=pair();p['XSupportingCandidateIds']=['same','same']
  with self.assertRaises(ValueError):self.group([p])
  with self.assertRaises(ValueError):self.group([pair()]*(MAX_CANDIDATES+1))
 def test_source_binding_affects_candidate_identity(self):
  a=self.group([pair()]);b=snapshot_group('c'*64,'b'*64,100,100,[deepcopy(pair())]);self.assertNotEqual(a['rows'][0]['candidate_identity_sha256'],b['rows'][0]['candidate_identity_sha256'])
if __name__=='__main__':unittest.main()
