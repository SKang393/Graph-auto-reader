import copy,unittest
from collections import Counter
from unittest import mock
from ml.synthetic.marker_context_catalog_v1 import catalog as c

class CatalogTests(unittest.TestCase):
    def test_fixed_seed_disjoint_and_balanced_recipe_grid(self):
        train,dev=c.recipes('train'),c.recipes('dev')
        self.assertEqual((len(train),len(dev)),(64,32))
        self.assertFalse({r['seed'] for r in train}&{r['seed'] for r in dev})
        for rows in (train,dev):
            counts=Counter((r['design'],r['connection_mode']) for r in rows)
            self.assertEqual(len(counts),8);self.assertEqual(len(set(counts.values())),1)
        self.assertEqual(c.catalog_sha256(),c.catalog_sha256())
        with self.assertRaises(ValueError):c.recipes('sealed')
    def test_scene_transform_is_copy_and_missing_edges_are_explicit(self):
        r=next(x for x in c.recipes('train') if x['connection_mode']=='missing_edges')
        scene={'seed':r['seed'],'design':r['design'],'scene_id':'aadd7e26-43a9-404f-8407-742185632bd3','families':{'renderer':{'key':'vector_clean'}},'presentation':{'connecting_line_style':'solid'},'provenance':{'source':'procedural','license':'Apache-2.0','private_data':False,'external_assets':[]},'panels':[{'series':[{'line_style':'solid'}],'connections':[{'style':'solid','visible':True}]}]}
        original=copy.deepcopy(scene)
        with mock.patch.object(c,'validate_scene') as check:
            result=c.prepare_scene(scene,r)
            self.assertEqual(check.call_count,2)
        self.assertEqual(scene,original);self.assertNotEqual(result['scene_id'],scene['scene_id'])
        self.assertEqual(result['panels'][0]['series'][0]['line_style'],'missing')
        self.assertEqual(result['presentation']['connecting_line_style'],'missing')
        self.assertEqual(result['panels'][0]['connections'][0],{'style':'missing','visible':False})
        wrong={**r,'seed':r['seed']+1000}
        with self.assertRaises(ValueError):c.prepare_scene(scene,wrong)

if __name__=='__main__':unittest.main()
