import copy, json, math, tempfile, types, unittest
from pathlib import Path
from unittest import mock
from ml.axis.gpu_candidate_ranker_v1 import trainer_v3 as t
from ml.axis.gpu_candidate_ranker_v1.test_contracts import ContractTests
from ml.markers.gate_seal import source_bundle_sha256

TEMP=t.ROOT/'artifacts'/'test-temp'
TEMP.mkdir(parents=True,exist_ok=True)
class AttemptTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory(dir=TEMP);self.root=Path(self.temp.name);self.seal=self.root/'seal';self.seal.mkdir();self.opened=self.seal/'opened.json';self.opened.write_bytes(b'opened')
        self.auth=types.SimpleNamespace(directory=self.seal,opened_path=self.opened);self.out=self.root/'attempt';self.patch=mock.patch.object(t,'OUT',self.out);self.patch.start();self.calls=[]
    def tearDown(self):self.patch.stop();self.temp.cleanup()
    def complete(self,auth,*,status,report_sha256):self.calls.append((status,report_sha256))
    def test_pre_intent_failure_finalizes_without_optimizer(self):
        original=t._write_new
        def write(path,raw):
            if path.name=='attempt.intent':raise OSError('injected intent failure')
            return original(path,raw)
        with mock.patch.object(t,'_write_new',side_effect=write),self.assertRaises(OSError):t.post_acquire(self.auth,b'config',1,lambda x:self.fail('operation called'),self.complete)
        outcome=json.loads((self.seal/'trainer-outcome.json').read_bytes());self.assertEqual(outcome['completed_steps'],0);self.assertEqual(self.calls[0][0],'failed_training_incomplete')
    def test_output_creation_failure_finalizes(self):
        original=Path.mkdir
        def mkdir(path,*args,**kwargs):
            if path==self.out:raise OSError('injected output failure')
            return original(path,*args,**kwargs)
        with mock.patch.object(Path,'mkdir',mkdir),self.assertRaises(OSError):t.post_acquire(self.auth,b'config',1,lambda x:self.fail('operation called'),self.complete)
        self.assertEqual(json.loads((self.seal/'trainer-outcome.json').read_bytes())['status'],'failed_training_incomplete');self.assertEqual(len(self.calls),1)
    def test_post_step_failure_and_persistent_single_use_marker(self):
        def work(completed):
            t.record_optimizer_start(self.auth,'a'*64);completed.append(0);raise ValueError('injected after step')
        with self.assertRaises(ValueError):t.post_acquire(self.auth,b'config',2,work,self.complete)
        self.assertEqual(json.loads((self.seal/'trainer-outcome.json').read_bytes())['completed_steps'],1)
        with self.assertRaises(FileExistsError):t.record_optimizer_start(self.auth,'a'*64)
    def test_finalization_failure_is_durable_and_not_success(self):
        def work(completed):
            completed.append(0);raw=b'checkpoint';(self.out/'checkpoint.json').write_bytes(raw)
            return {'status':'training_complete_pending_finalization','steps':1},lambda:t._sha(raw)
        def fail(*args,**kwargs):raise OSError('ledger failure')
        with mock.patch('ml.axis.candidate_ranker_foundation_v1.train_labels_v2.lock'),self.assertRaises(OSError):t.post_acquire(self.auth,b'config',1,work,fail)
        marker=json.loads((self.seal/'trainer-finalization-failed.json').read_bytes());self.assertFalse(marker['ledger_finalized']);self.assertEqual(marker['status'],'finalization_failed')
        self.assertEqual(json.loads((self.out/'report.json').read_bytes())['status'],'training_complete_pending_finalization');self.assertFalse((self.out/'finalization.json').exists())
    def test_post_training_report_or_outcome_write_is_finalization_failure(self):
        original=t._write_new
        for failed in ('report.json','trainer-outcome.json'):
            with self.subTest(failed=failed),tempfile.TemporaryDirectory(dir=TEMP) as folder:
                base=Path(folder);seal=base/'seal';seal.mkdir();opened=seal/'opened.json';opened.write_bytes(b'opened');auth=types.SimpleNamespace(directory=seal,opened_path=opened);out=base/'out';calls=[]
                def work(done):
                    done.append(0);raw=b'checkpoint';(out/'checkpoint.json').write_bytes(raw)
                    return {'status':'training_complete_pending_finalization','steps':1},lambda:t._sha(raw)
                def write(path,raw):
                    if path.name==failed:raise OSError('injected write failure')
                    return original(path,raw)
                with mock.patch.object(t,'OUT',out),mock.patch.object(t,'_write_new',side_effect=write),mock.patch('ml.axis.candidate_ranker_foundation_v1.train_labels_v2.lock'),self.assertRaises(OSError):t.post_acquire(auth,b'config',1,work,lambda *a,**k:calls.append(1))
                marker=json.loads((seal/'trainer-finalization-failed.json').read_bytes());self.assertEqual(marker['status'],'finalization_failed');self.assertFalse(marker['ledger_finalized']);self.assertEqual(calls,[])
                self.assertFalse((seal/'trainer-outcome.json').exists() and json.loads((seal/'trainer-outcome.json').read_bytes())['status']=='failed_training_incomplete')
    def test_ledger_success_then_receipt_failure_reports_finalized(self):
        original=t._write_new
        def work(done):
            done.append(0);raw=b'checkpoint';(self.out/'checkpoint.json').write_bytes(raw)
            return {'status':'training_complete_pending_finalization','steps':1},lambda:t._sha(raw)
        def write(path,raw):
            if path.name=='finalization.json':raise OSError('receipt failed')
            return original(path,raw)
        with mock.patch.object(t,'_write_new',side_effect=write),mock.patch('ml.axis.candidate_ranker_foundation_v1.train_labels_v2.lock'),self.assertRaises(OSError):t.post_acquire(self.auth,b'config',1,work,self.complete)
        marker=json.loads((self.seal/'trainer-finalization-failed.json').read_bytes());self.assertTrue(marker['ledger_finalized']);self.assertEqual(marker['status'],'post_finalization_receipt_failed');self.assertEqual(len(self.calls),1)
    def test_failure_after_last_completed_step_is_finalization_failure(self):
        def work(done):done.append(0);raise OSError('failed after final step')
        with self.assertRaises(OSError):t.post_acquire(self.auth,b'config',1,work,self.complete)
        marker=json.loads((self.seal/'trainer-finalization-failed.json').read_bytes());self.assertEqual(marker['status'],'finalization_failed');self.assertEqual(self.calls,[])
    def test_checkpoint_is_locked_through_ledger_completion(self):
        state={'locked':False}
        def fake_lock(stack,path):state['locked']=True;stack.callback(lambda:state.update(locked=False))
        def work(done):
            done.append(0);raw=b'checkpoint';(self.out/'checkpoint.json').write_bytes(raw)
            return {'status':'training_complete_pending_finalization','steps':1},lambda:t._sha(raw)
        def completed(auth,**kwargs):self.assertTrue(state['locked']);self.calls.append(kwargs)
        with mock.patch('ml.axis.candidate_ranker_foundation_v1.train_labels_v2.lock',side_effect=fake_lock):t.post_acquire(self.auth,b'config',1,work,completed)
        self.assertFalse(state['locked']);self.assertEqual(json.loads((self.out/'finalization.json').read_bytes())['status'],'finalized')
    def test_optimizer_marker_survives_moved_output(self):
        seal=self.root/'ml/markers/training-seals'/t.TASK/t.REVISION/t.CANDIDATE;seal.mkdir(parents=True);opened=seal/'opened.json';opened.write_bytes(b'open');auth=types.SimpleNamespace(directory=seal,opened_path=opened);t.record_optimizer_start(auth,'a'*64)
        with mock.patch.object(t,'ROOT',self.root),mock.patch.object(t,'OUT',self.root/'missing-attempt'),self.assertRaisesRegex(RuntimeError,'second attempt'):
            t.run_training()
    def test_source_and_config_drift_and_scope_rejected(self):
        f=self.root/'protocol.json';f.write_text('one');before=source_bundle_sha256(self.root,[Path('protocol.json')]);f.write_text('two');after=source_bundle_sha256(self.root,[Path('protocol.json')]);self.assertNotEqual(before,after)
        config=t.fixed_config(before)
        with self.assertRaises(ValueError):t.validate_config(config,after)
        data={'groups_sha256':t.GROUP_SHA,'targets_sha256':t.TARGET_SHA,'groups':93,'candidates':27776,'dev_read':False,'reserved_read':False,'optimizer_steps':0,'training_admitted':False};t.validate_data_receipt(data)
        for key in ('dev_read','reserved_read','training_admitted'):
            d=copy.deepcopy(data);d[key]=True
            with self.assertRaises(RuntimeError):t.validate_data_receipt(d)

class OptimizerGuardTests(unittest.TestCase):
    def test_ties_are_retained_and_short_group_rejected(self):
        c=ContractTests();c.setUp();c.doc['groups'][0]['rows'][1]['target_error']=c.doc['groups'][0]['rows'][0]['target_error'];self.assertEqual(len(c.parse()[0]['rows']),2)
        c.doc['groups'][0]['rows'].pop()
        with self.assertRaises(ValueError):c.parse()
    def test_optimizer_config_device_and_nonfinite_state(self):
        class Tensor:
            def __init__(self,device='cuda:0',finite=True):self.device=device;self.finite=finite
        class AdamW:
            def __init__(self,p):self.param_groups=[{'params':p,'lr':t.LEARNING_RATE,'weight_decay':t.WEIGHT_DECAY,'capturable':True,'foreach':False,'fused':False}];self.state={}
        class Result:
            def __init__(self,value):self.value=value
            def all(self):return self
            def item(self):return self.value
        torch=types.SimpleNamespace(Tensor=Tensor,optim=types.SimpleNamespace(AdamW=AdamW),isfinite=lambda x:Result(x.finite))
        p=Tensor();model=types.SimpleNamespace(parameters=lambda:iter((p,)));opt=AdamW([p])
        def guard(_torch,*values):
            if any(v.device!='cuda:0' for v in values):raise RuntimeError('off-device')
        with mock.patch.object(t,'require_gpu',side_effect=guard):
            t.check_optimizer(torch,model,opt)
            opt.param_groups[0]['capturable']=False
            with self.assertRaises(RuntimeError):t.check_optimizer(torch,model,opt)
            opt.param_groups[0]['capturable']=True;opt.state={'x':{'v':Tensor('cpu')}}
            with self.assertRaises(RuntimeError):t.check_optimizer(torch,model,opt)
            opt.state={'x':{'v':Tensor(finite=False)}}
            with self.assertRaises(RuntimeError):t.check_optimizer(torch,model,opt)
    def test_listwise_equal_cost_and_invalid_shapes_without_backward(self):
        import torch
        with mock.patch.object(t,'require_gpu'):
            logits=torch.tensor([3.0,1.0],device='cpu');cost=torch.tensor([0.25,0.25],device='cpu')
            observed=float(t.listwise_loss(torch,logits,cost).item())
            expected=math.log(math.exp(3)+math.exp(1))-2
            self.assertAlmostEqual(observed,expected,places=6)
            with self.assertRaises(ValueError):t.listwise_loss(torch,logits,torch.tensor([0.0]))
            with self.assertRaises(ValueError):t.listwise_loss(torch,logits,torch.tensor([0.0,float('nan')]))
            with self.assertRaises(ValueError):t.listwise_loss(torch,torch.tensor([0.0]),torch.tensor([0.0]))
    def test_previous_checked_in_config_matches_previous_source_bundle(self):
        from ml.axis.gpu_candidate_ranker_v1 import trainer_v2 as previous
        bundle=source_bundle_sha256(previous.ROOT,previous.SOURCES)
        previous.validate_config(json.loads((previous.ROOT/previous.CONFIG).read_bytes()),bundle)
    def test_current_checked_in_config_matches_current_source_bundle(self):
        bundle=source_bundle_sha256(t.ROOT,t.SOURCES)
        self.assertEqual(json.loads((t.ROOT/t.CONFIG).read_bytes()),t.fixed_config(bundle))

class AtomicCheckpointTests(unittest.TestCase):
    def test_partial_alias_removed_and_readback_locked(self):
        class Parameter:
            def __init__(self,shape,values):self.shape=shape;self.values=values
            def detach(self):return self
            def cpu(self):return self
            def reshape(self,_):return self
            def tolist(self):return self.values
            def item(self):return self.values[0]
        model=types.SimpleNamespace(named_parameters=lambda:iter((('weight',Parameter((1,17),[0.0]*17)),('bias',Parameter((1,),[0.0])))))
        with tempfile.TemporaryDirectory(dir=TEMP) as folder:
            out=Path(folder);state={'locked':False}
            def fake_lock(stack,path):state['locked']=True;self.assertFalse((out/'checkpoint.partial').exists());stack.callback(lambda:state.update(locked=False))
            with mock.patch.object(t,'OUT',out),mock.patch.object(t,'require_gpu'),mock.patch('ml.axis.candidate_ranker_foundation_v1.train_labels_v2.lock',side_effect=fake_lock):
                digest=t.atomic_checkpoint(object(),model,'a'*64)
            self.assertFalse(state['locked']);self.assertFalse((out/'checkpoint.partial').exists());self.assertEqual(t._sha((out/'checkpoint.json').read_bytes()),digest)

if __name__=='__main__':unittest.main()
