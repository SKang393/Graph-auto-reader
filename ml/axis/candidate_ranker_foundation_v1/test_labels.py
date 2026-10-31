import math,unittest
from ml.axis.candidate_ranker_foundation_v1.labels import associate,continuous_targets
def plot(id,x=0,y=0):return {'id':id,'polygon':[[x,y],[x+100,y],[x+100,y+100],[x,y+100]]}
def panel(id,crop,count=2,success=True):return {'id':id,'crop':crop,'exporter_success':success,'candidate_count':count if success else None}
def pair(dx=0):return {'Intersection':{'X':dx,'Y':100},'BottomRight':{'X':100+dx,'Y':100},'TopRight':{'X':100+dx,'Y':0},'TopLeft':{'X':dx,'Y':0}}
class LabelTests(unittest.TestCase):
 def test_unique_and_missing_and_unmatched(self):
  r=associate('detected',[plot('a'),plot('b',200)],[panel('p',[0,0,150,150]),panel('extra',[400,0,100,100])])
  self.assertEqual(r['matches'],[{'panel_id':'p','plot_id':'a'}]);self.assertEqual(r['summary']['missed_plots'],1);self.assertEqual(r['summary']['unmatched_panels'],1)
 def test_many_to_one_is_not_labeled(self):
  r=associate('detected',[plot('a'),plot('b',200)],[panel('p',[0,0,400,150])])
  self.assertEqual(r['matches'],[]);self.assertEqual(r['summary']['ambiguous_plots'],2);self.assertEqual(r['summary']['ambiguous_panels'],1)
 def test_duplicate_covering_panels_are_ambiguous(self):
  r=associate('detected',[plot('a')],[panel('p',[0,0,150,150]),panel('q',[0,0,150,150])])
  self.assertEqual(r['matches'],[]);self.assertEqual(r['summary']['ambiguous_plots'],1);self.assertEqual(r['summary']['ambiguous_panels'],2)
 def test_fallback_and_failure_are_distinct(self):
  fallback=associate('no_panel_fallback',[plot('a')],[panel('p',[0,0,200,200])]);failed=associate('failed',[plot('a')],[])
  self.assertEqual(fallback['summary']['missed_plots'],1);self.assertEqual(failed['summary']['unknown_plots'],1);self.assertEqual(failed['summary']['missed_plots'],0)
 def test_export_failure_and_overflow_remain_recorded(self):
  failed=associate('detected',[plot('a')],[panel('p',[0,0,200,200],success=False)]);overflow=associate('detected',[plot('a')],[panel('p',[0,0,200,200],count=16385)])
  self.assertEqual(failed['summary']['exporter_failures'],1);self.assertEqual(overflow['summary']['overflow_groups'],1);self.assertEqual(overflow['matches'],[])
 def test_continuous_targets_keep_order_and_reject_bad_truth(self):
  self.assertEqual(continuous_targets(plot('a'),[0,0,100,100],[pair(),pair(10)]),[0,.1])
  bad=plot('a');bad['polygon'][0][0]=math.nan
  with self.assertRaises(ValueError):continuous_targets(bad,[0,0,100,100],[pair()])
  bad=plot('a');bad['polygon'][1],bad['polygon'][2]=bad['polygon'][2],bad['polygon'][1]
  with self.assertRaises(ValueError):continuous_targets(bad,[0,0,100,100],[pair()])
if __name__=='__main__':unittest.main()
