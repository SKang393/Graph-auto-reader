import copy, hashlib, json, unittest
from ml.axis.candidate_ranker_foundation_v1.features import FEATURE_NAMES
from ml.axis.gpu_candidate_ranker_v1.contracts import Recipe, parse_groups, schedule
from ml.axis.gpu_candidate_ranker_v1 import runner

class ContractTests(unittest.TestCase):
    def setUp(self):
        self.source='1'*64;self.panel='2'*64
        self.doc={'schema':'graphreader.axis-ranker-training-groups.draft-v1','split':'train','feature_names':list(FEATURE_NAMES),'groups':[{'source_sha256':self.source,'panel_sha256':self.panel,'rows':[{'rank':i,'candidate_identity_sha256':str(i+2)*64,'features':[0.0]*17,'target_error':float(i)} for i in (1,2)]}]}
        self.allowed=frozenset({(self.source,self.panel)})
    def parse(self,doc=None,allowed=None):
        raw=json.dumps(self.doc if doc is None else doc,allow_nan=True).encode();recipe=Recipe(7,2,0.001,hashlib.sha256(raw).hexdigest())
        return parse_groups(raw,recipe,self.allowed if allowed is None else allowed)
    def test_good_group_and_complete_deterministic_schedule(self):
        self.assertEqual(len(self.parse()),1)
        recipe=Recipe(7,3,0.001,'a'*64);order=schedule(recipe,4)
        self.assertEqual(order,schedule(recipe,4))
        for i in range(0,12,4):self.assertEqual(set(order[i:i+4]),set(range(4)))
    def test_digest_inventory_and_malformed_rows_fail(self):
        raw=json.dumps(self.doc).encode()
        with self.assertRaises(ValueError):parse_groups(raw,Recipe(1,1,0.001,'a'*64),self.allowed)
        with self.assertRaises(ValueError):self.parse(allowed=frozenset({('a'*64,'b'*64)}))
        for mutate in ('rank','features','target','duplicate','split'):
            doc=copy.deepcopy(self.doc);rows=doc['groups'][0]['rows']
            if mutate=='rank':rows[1]['rank']=1
            if mutate=='features':rows[0]['features'][0]=float('nan')
            if mutate=='target':rows[0]['target_error']=-1
            if mutate=='duplicate':rows[1]['candidate_identity_sha256']=rows[0]['candidate_identity_sha256']
            if mutate=='split':doc['split']='validation'
            with self.subTest(mutate=mutate),self.assertRaises(ValueError):self.parse(doc)
    def test_duplicate_json_key_fails(self):
        raw=b'{"schema":1,"schema":2}';recipe=Recipe(1,1,0.001,hashlib.sha256(raw).hexdigest())
        with self.assertRaises(ValueError):parse_groups(raw,recipe,self.allowed)
    def test_training_blocked_without_creating_model(self):
        with self.assertRaisesRegex(RuntimeError,'canonical training admission'):runner.run_training()

if __name__=='__main__':unittest.main()
