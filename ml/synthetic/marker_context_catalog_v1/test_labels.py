import copy,struct,unittest
from ml.synthetic.marker_context_catalog_v1.labels import PATCH_BYTES,classify,mark_local_collisions

def fixture():
    return {'scene_id':'scene','coordinate_space':'original_pixels','canvas':{'width':100,'height':40},'contextual_negative_profile':{'id':'synthetic-contextual-negatives-v1','split':'train','source_scene_id':'scene'},'panels':[{'panel_id':'panel','markers':[{'marker_id':'a','point_id':'1','panel_id':'panel','center':[10,10],'coordinate_space':'original_pixels'},{'marker_id':'b','point_id':'2','panel_id':'panel','center':[20,10],'coordinate_space':'original_pixels'},{'marker_id':'c','point_id':'3','panel_id':'panel','center':[30,10],'coordinate_space':'original_pixels'}],'edges':[{'from_marker_id':'1','to_marker_id':'2','drawn':True},{'from_marker_id':'2','to_marker_id':'3','drawn':False}],'legends':[{'legend_id':'leg','visible':True,'box':[40,0,40,20],'entries':[{'text':'A','text_id':None,'glyph_center':[45,10],'glyph_box':[42,7,6,6]}]}],'texts':[{'text_id':'text','role':'legend_text','visible':True,'text':'A','rendered_pixel_box':[52,5,10,8]}]}], 'hard_negatives':[{'hard_negative_id':'neg','panel_id':'panel','kind':'legend_symbol','source_reference':'leg:0','geometry':{'center':[45,10],'box':[42,7,6,6]},'context':{'category':'legends','legend_id':'leg','text_ids':['text']},'coordinate_space':'original_pixels','excluded_from_marker_mask':True}]}

class LabelTests(unittest.TestCase):
    def test_connected_isolated_and_actual_legend_context(self):
        result=classify(fixture(),expected_split='train');self.assertEqual(result['counts'],{'connected_positive':2,'isolated_positive':1,'contextual_negative':1});self.assertFalse(result['optimization_allowed']);self.assertFalse(result['runtime_parity_verified'])
        with self.assertRaises(ValueError):classify(fixture(),expected_split='dev')
    def test_missing_context_and_wrong_geometry_fail(self):
        for variant in ('text','geometry','provenance','edge'):
            d=fixture()
            if variant=='text':d['panels'][0]['texts'][0]['text']='other'
            if variant=='geometry':d['hard_negatives'][0]['geometry']['center']=[46,10]
            if variant=='provenance':d['hard_negatives'][0]['excluded_from_marker_mask']=False
            if variant=='edge':d['panels'][0]['edges'][0]['from_marker_id']='unknown'
            with self.subTest(variant=variant),self.assertRaises(ValueError):classify(d,expected_split='train')
    def test_absent_legend_is_counted_without_fabrication(self):
        d=fixture();d['hard_negatives']=[];result=classify(d,expected_split='train')
        self.assertIn('contextual_negative',result['missing_bins'])
    def test_exact_cross_label_collision_is_review_only_and_does_not_relabel(self):
        records=classify(fixture(),expected_split='train')['records'];zero=bytes(PATCH_BYTES);one=struct.pack('<f',1.0)*(PATCH_BYTES//4)
        patches={r['id']:one for r in records};patches['marker:c']=zero;patches['negative:neg']=zero
        result={r['id']:r for r in mark_local_collisions(records,patches)}
        self.assertTrue(result['marker:c']['review_only_local_identity_collision']);self.assertEqual(result['marker:c']['label'],1);self.assertEqual(result['marker:c']['supervision'],'review_only')
        self.assertTrue(result['negative:neg']['review_only_local_identity_collision']);self.assertFalse(result['marker:a']['review_only_local_identity_collision'])
        with self.assertRaises(ValueError):mark_local_collisions(records,{'marker:c':zero})
        patches['marker:a']=struct.pack('<f',float('nan'))*(PATCH_BYTES//4)
        with self.assertRaises(ValueError):mark_local_collisions(records,patches)

if __name__=='__main__':unittest.main()
