import copy, unittest
from ml.axis.gpu_candidate_ranker_v1.prepare_data import join_group

class Features:
    @staticmethod
    def snapshot_group(source,panel,w,h,snapshots):
        return {'rows':[{'rank':i+1,'candidate_identity_sha256':str(i+1)*64,'features':[float(i)]*17} for i in range(len(snapshots))]}

class JoinTests(unittest.TestCase):
    def setUp(self):
        self.panel={'status':'success','source_sha256':'a'*64,'panel_sha256':'b'*64,'panel_width':10,'panel_height':10,'candidate_snapshots':[{'Rank':0},{'Rank':1}],'normalized_features':{'rows':[{'rank':1,'features':[0.0]*17},{'rank':2,'features':[1.0]*17}]}}
        self.target={'source_sha256':'a'*64,'panel_sha256':'b'*64,'rows':[{'rank':1,'candidate_identity_sha256':'1'*64,'target_error':0.1},{'rank':2,'candidate_identity_sha256':'2'*64,'target_error':0.2}]}
    def test_join_preserves_costs_and_all_ranks(self):
        rows=join_group(self.panel,self.target,Features)['rows'];self.assertEqual([r['target_error'] for r in rows],[0.1,0.2]);self.assertEqual([r['rank'] for r in rows],[1,2])
    def test_substitution_or_wrong_rank_rejected(self):
        for field in ('candidate','rank','feature','source'):
            p,t=copy.deepcopy(self.panel),copy.deepcopy(self.target)
            if field=='candidate':t['rows'][0]['candidate_identity_sha256']='3'*64
            if field=='rank':p['candidate_snapshots'][0]['Rank']=1
            if field=='feature':p['normalized_features']['rows'][0]['features'][0]=0.1
            if field=='source':t['source_sha256']='c'*64
            with self.subTest(field=field),self.assertRaises(ValueError):join_group(p,t,Features)

if __name__=='__main__':unittest.main()
