import importlib.util, unittest
from pathlib import Path

PATH=Path(__file__).with_name('extract_train_v1.py')
spec=importlib.util.spec_from_file_location('extract_train_v1',PATH)
module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)

class SelectionTests(unittest.TestCase):
    def fixture(self):
        sources=[]
        for i in range(64):
            panels=[{'panel_id':f'{i}-0'}]
            if i<32:panels.append({'panel_id':f'{i}-1'})
            sources.append({'id':str(i),'source_status':'detected','panels':panels})
        return {'truth_read':False,'dev_read':False,'reserved_read':False,'optimizer_steps':0,'training_admitted':False,'sources':sources}
    def test_all_sources_and_panels_accounted(self):
        result=module.selections(self.fixture());self.assertEqual(len(result),64);self.assertEqual(sum(len(p) for _,p in result),96)
    def test_restricted_scope_and_duplicate_rejected(self):
        doc=self.fixture();doc['dev_read']=True
        with self.assertRaises(ValueError):module.selections(doc)
        doc=self.fixture();doc['sources'][0]['panels'][1]['panel_id']=doc['sources'][0]['panels'][0]['panel_id']
        with self.assertRaises(ValueError):module.selections(doc)

if __name__=='__main__':unittest.main()
