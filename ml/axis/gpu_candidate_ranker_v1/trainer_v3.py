# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Unapproved canonical-admission-only RX 6600 XT linear group ranker.

No execution occurs on import. The caller must separately register the exact
config/source bundle in the canonical training ledger before invoking main.
"""
from hashlib import sha256
from pathlib import Path
import json, math, os, random, sys

ROOT=Path(__file__).resolve().parents[3]
TASK='axis-candidate-ranker'
REVISION='axis-candidate-linear-gpu-v1'
CANDIDATE='P1'
CONFIG=Path('ml/axis/gpu_candidate_ranker_v1/p1-v4.json')
GROUPS=Path('artifacts/goal22-runs/axis-ranker-gpu-training-data-v1/groups.json')
DATA_RECEIPT=Path('artifacts/goal22-runs/axis-ranker-gpu-training-data-v1/receipt.json')
OUT=ROOT/'artifacts/goal22-runs/axis-ranker-gpu-training-attempt-v1'
SOURCES=tuple(Path(p) for p in ('ml/axis/gpu_candidate_ranker_v1/__init__.py','ml/axis/gpu_candidate_ranker_v1/contracts.py','ml/axis/gpu_candidate_ranker_v1/trainer_v3.py','ml/axis/gpu_candidate_ranker_v1/protocol.json','ml/axis/candidate_ranker_foundation_v1/__init__.py','ml/axis/candidate_ranker_foundation_v1/features.py','ml/axis/candidate_ranker_foundation_v1/train_labels_v2.py','ml/axis/__init__.py','ml/markers/training_budget.py','ml/markers/gate_seal.py','ml/policy/evidence_policy.py','ml/policy/evidence-policy.json','ml/policy/acceptance-bars.json'))
GROUP_SHA='d9c5928ffea0407fd5b1041d3c3ca9146f128d71054cca0026a51e88a3dc67b5'
RECEIPT_SHA='2f3eadd30b19fd8d86930571f230e01fe8fb8832bce999e4c98759ca21859622'
TARGET_SHA='15193d25c5346799d16aadbaaffb303cdcb0b361aa2a7ebdedf336d706289582'
SEED=20261031
EPOCHS=4
LEARNING_RATE=0.001
WEIGHT_DECAY=0.0001
TEMPERATURE=0.1
SCALE=(1.0,)*15+(0.03125,0.03125)

def _sha(raw):return sha256(raw).hexdigest()
def _canonical(value):return (json.dumps(value,sort_keys=True,separators=(',',':'),allow_nan=False)+'\n').encode()
def _write_new(path,raw):
    with path.open('xb') as stream:stream.write(raw);stream.flush();os.fsync(stream.fileno())
def record_optimizer_start(authorization,config_sha):
    _write_new(authorization.directory/'optimizer-start.intent',_canonical({'schema':'graphreader.axis-gpu-optimizer-start.v1','task':TASK,'revision':REVISION,'candidate_id':CANDIDATE,'config_sha256':config_sha,'opened_sha256':_sha(authorization.opened_path.read_bytes()),'resume_authorized':False}))
def validate_data_receipt(data):
    if data['groups_sha256']!=GROUP_SHA or data['targets_sha256']!=TARGET_SHA or data['groups']!=93 or data['candidates']!=27776 or data['dev_read'] is not False or data['reserved_read'] is not False or data['optimizer_steps']!=0 or data['training_admitted'] is not False:raise RuntimeError('Training data provenance differs')
def fixed_config(bundle_sha):
    return {'schema':'graphreader.axis-gpu-ranker-config.v1','task':TASK,'revision':REVISION,'candidate_id':CANDIDATE,'groups_path':GROUPS.as_posix(),'groups_sha256':GROUP_SHA,'data_receipt_path':DATA_RECEIPT.as_posix(),'data_receipt_sha256':RECEIPT_SHA,'target_sha256':TARGET_SHA,'group_count':93,'candidate_count':27776,'seed':SEED,'epochs':EPOCHS,'optimizer_steps':372,'learning_rate':LEARNING_RATE,'weight_decay':WEIGHT_DECAY,'target_temperature':TEMPERATURE,'feature_scale':list(SCALE),'architecture':'linear-17-to-1','objective':'listwise-soft-target-cross-entropy','expected_runner_source_bundle_sha256':bundle_sha,'checkpoint_path':'artifacts/goal22-runs/axis-ranker-gpu-training-attempt-v1/checkpoint.json','dev_read':False,'reserved_read':False,'production_approved':False}
def validate_config(config,bundle_sha):
    if config!=fixed_config(bundle_sha):raise ValueError('Candidate config differs from fixed recipe')
def require_gpu(torch,*values):
    device=torch.device('cuda:0')
    if not torch.cuda.is_available() or not torch.version.hip or torch.cuda.device_count()<1 or torch.cuda.current_device()!=0:raise RuntimeError('HIP RX 6600 XT cuda:0 required')
    props=torch.cuda.get_device_properties(device)
    if props.name!='AMD Radeon RX 6600 XT' or props.gcnArchName.split(':')[0]!='gfx1032':raise RuntimeError('Wrong GPU')
    if any(not isinstance(v,torch.Tensor) or v.device!=device for v in values):raise RuntimeError('Off-device training tensor')
def check_optimizer(torch,model,optimizer):
    if type(optimizer) is not torch.optim.AdamW or len(optimizer.param_groups)!=1:raise RuntimeError('Wrong optimizer')
    group=optimizer.param_groups[0];params=tuple(model.parameters());actual=group['params']
    if len(actual)!=len(params) or {id(x) for x in actual}!={id(x) for x in params} or group['lr']!=LEARNING_RATE or group['weight_decay']!=WEIGHT_DECAY or group.get('capturable') is not True or group.get('foreach') is not False or group.get('fused') is not False:raise RuntimeError('Optimizer configuration differs')
    state=[v for s in optimizer.state.values() for v in s.values() if isinstance(v,torch.Tensor)]
    require_gpu(torch,*params,*state)
    if any(not bool(torch.isfinite(v).all().item()) for v in state):raise RuntimeError('Nonfinite optimizer state')
def listwise_loss(torch,logits,costs):
    require_gpu(torch,logits,costs)
    if logits.ndim!=1 or costs.shape!=logits.shape or logits.numel()<2 or not bool(torch.isfinite(logits).all().item()) or not bool(torch.isfinite(costs).all().item()):raise ValueError('Invalid listwise inputs')
    target=torch.softmax(-costs/TEMPERATURE,dim=0)
    loss=-(target*torch.log_softmax(logits,dim=0)).sum()
    require_gpu(torch,target,loss)
    if not bool(torch.isfinite(loss).item()):raise RuntimeError('Nonfinite loss')
    return loss
def atomic_checkpoint(torch,model,config_sha):
    from ml.axis.candidate_ranker_foundation_v1.train_labels_v2 import lock
    from contextlib import ExitStack
    params=dict(model.named_parameters());require_gpu(torch,*params.values())
    if set(params)!={'weight','bias'} or tuple(params['weight'].shape)!=(1,17) or tuple(params['bias'].shape)!=(1,):raise RuntimeError('Model topology differs')
    values=[float(v) for v in params['weight'].detach().cpu().reshape(-1).tolist()];bias=float(params['bias'].detach().cpu().item())
    if not all(math.isfinite(x) for x in (*values,bias)):raise RuntimeError('Checkpoint contains nonfinite value')
    raw=_canonical({'schema':'graphreader.axis-linear-ranker-checkpoint.v1','config_sha256':config_sha,'feature_scale':list(SCALE),'weight':values,'bias':bias,'production_approved':False})
    tmp=OUT/'checkpoint.partial';dest=OUT/'checkpoint.json';_write_new(tmp,raw);os.link(tmp,dest);tmp.unlink()
    with ExitStack() as stack:
        lock(stack,dest)
        if _sha(dest.read_bytes())!=_sha(raw):raise RuntimeError('Checkpoint readback differs')
    return _sha(raw)
def post_acquire(authorization,config_raw,steps,operation,complete):
    """Finalize every post-acquisition failure, including output setup errors."""
    from contextlib import ExitStack
    from ml.axis.candidate_ranker_foundation_v1.train_labels_v2 import lock
    completed=[];finalization_started=False;ledger_finalized=False;owned_output=False
    try:
        OUT.mkdir();owned_output=True
        _write_new(OUT/'attempt.intent',_canonical({'config_sha256':_sha(config_raw),'opened_sha256':_sha(authorization.opened_path.read_bytes()),'steps':steps,'resumable':False}))
        report,checkpoint_writer=operation(completed)
        if len(completed)==steps:finalization_started=True
        if len(completed)!=steps or report['steps']!=steps or report['status']!='training_complete_pending_finalization':raise RuntimeError('Completed step count or pending report status differs')
        report['checkpoint_sha256']=checkpoint_writer()
        # Keep the validated checkpoint locked until canonical finalization.
        with ExitStack() as stack:
            checkpoint=OUT/'checkpoint.json';lock(stack,checkpoint)
            if _sha(checkpoint.read_bytes())!=report['checkpoint_sha256']:raise RuntimeError('Checkpoint changed before finalization')
            raw=_canonical(report);_write_new(OUT/'report.json',raw)
            _write_new(authorization.directory/'trainer-outcome.json',raw)
            complete(authorization,status='training_complete_unapproved',report_sha256=_sha(raw))
            ledger_finalized=True
            _write_new(OUT/'finalization.json',_canonical({'schema':'graphreader.axis-gpu-ranker-finalization.v1','status':'finalized','report_sha256':_sha(raw),'checkpoint_sha256':report['checkpoint_sha256'],'production_approved':False}))
            return _sha(raw)
    except BaseException as error:
        if len(completed)==steps:finalization_started=True
        if finalization_started:
            marker=_canonical({'schema':'graphreader.axis-gpu-ranker-finalization-failure.v1','status':'finalization_failed' if not ledger_finalized else 'post_finalization_receipt_failed','exception_type':type(error).__name__,'exception_sha256':_sha(str(error).encode()),'ledger_finalized':ledger_finalized,'resume_authorized':False})
            try:_write_new(authorization.directory/'trainer-finalization-failed.json',marker)
            except BaseException as marker_error:error.add_note('Could not persist finalization failure: '+type(marker_error).__name__)
            raise
        failure={'schema':'graphreader.axis-gpu-ranker-training-report.v1','status':'failed_training_incomplete','completed_steps':len(completed),'exception_type':type(error).__name__,'production_approved':False,'resume_authorized':False}
        raw=_canonical(failure)
        try:
            # This authoritative copy is independent of the output folder,
            # which may not exist or may have caused the original failure.
            outcome=authorization.directory/'trainer-outcome.json'
            if outcome.exists():raise RuntimeError('Existing trainer outcome prevents failure finalization')
            _write_new(outcome,raw)
            if owned_output:
                try:_write_new(OUT/'failure.json',raw)
                except BaseException as output_error:error.add_note('Could not persist secondary failure report: '+type(output_error).__name__)
            complete(authorization,status='failed_training_incomplete',report_sha256=_sha(raw))
        except BaseException as final_error:
            marker=_canonical({'schema':'graphreader.axis-gpu-ranker-finalization-failure.v1','status':'finalization_failed','exception_type':type(final_error).__name__,'original_exception_type':type(error).__name__,'original_exception_sha256':_sha(str(error).encode()),'resume_authorized':False})
            try:_write_new(authorization.directory/'trainer-finalization-failed.json',marker)
            except BaseException as marker_error:error.add_note('Could not persist finalization failure: '+type(marker_error).__name__)
            error.add_note('Canonical finalization failed: '+type(final_error).__name__)
        raise
def run_training():
    """Acquire registered candidate, run one crash-aware non-resumable attempt."""
    from ml.markers.gate_seal import source_bundle_sha256,verify_bound_source_snapshot
    from ml.markers.training_budget import acquire_training_candidate,complete_training_candidate
    from ml.axis.gpu_candidate_ranker_v1.contracts import Recipe,parse_groups,schedule
    from ml.axis.candidate_ranker_foundation_v1.train_labels_v2 import lock,safe
    from contextlib import ExitStack
    if OUT.exists():raise RuntimeError('A prior attempt exists; resume is forbidden')
    seal=ROOT/'ml/markers/training-seals'/TASK/REVISION/CANDIDATE
    if (seal/'optimizer-start.intent').exists():raise RuntimeError('Optimizer already started; a second attempt is forbidden')
    # CPU input decoding and tensor staging are not model training.
    with ExitStack() as stack:
        for p in (ROOT/CONFIG,ROOT/GROUPS,ROOT/DATA_RECEIPT,ROOT/'ml/markers/training-budgets/production-repair-v1.json',*(ROOT/name for name in SOURCES)):lock(stack,p)
        config_raw=(ROOT/CONFIG).read_bytes();config=json.loads(config_raw);bundle=source_bundle_sha256(ROOT,SOURCES);validate_config(config,bundle)
        groups_raw=(ROOT/GROUPS).read_bytes();data_raw=(ROOT/DATA_RECEIPT).read_bytes()
        if _sha(groups_raw)!=GROUP_SHA or _sha(data_raw)!=RECEIPT_SHA:raise RuntimeError('Training inputs differ')
        data=json.loads(data_raw)
        validate_data_receipt(data)
        document=json.loads(groups_raw);allowed=frozenset((g['source_sha256'],g['panel_sha256']) for g in document['groups']);recipe=Recipe(SEED,EPOCHS,LEARNING_RATE,GROUP_SHA);groups=parse_groups(groups_raw,recipe,allowed);order=schedule(recipe,len(groups))
        if len(groups)!=93 or sum(len(x['rows']) for x in groups)!=27776 or len(order)!=372:raise RuntimeError('Training group coverage differs')
        # Hardware check happens before candidate acquisition or model creation.
        import torch
        require_gpu(torch);torch.use_deterministic_algorithms(True);random.seed(SEED);torch.manual_seed(SEED);torch.cuda.manual_seed_all(SEED)
        safe(OUT.parent)
        authorization=acquire_training_candidate(ROOT,task=TASK,revision=REVISION,candidate_id=CANDIDATE,config_path=CONFIG,runner_source_paths=SOURCES)
        def operation(losses):
            if (authorization.directory/'optimizer-start.intent').exists():raise RuntimeError('Optimizer already started; a second attempt is forbidden')
            model=torch.nn.Linear(17,1,device=torch.device('cuda:0'),dtype=torch.float32);require_gpu(torch,*model.parameters())
            optimizer=torch.optim.AdamW(model.parameters(),lr=LEARNING_RATE,weight_decay=WEIGHT_DECAY,capturable=True,foreach=False,fused=False)
            if optimizer.state:raise RuntimeError('Optimizer is not fresh')
            for index,group_index in enumerate(order):
                verify_bound_source_snapshot(ROOT,authorization.snapshot_path,authorization.binding['source_snapshot_sha256'])
                require_gpu(torch,*model.parameters());check_optimizer(torch,model,optimizer)
                rows=groups[group_index]['rows'];features=torch.tensor([r['features'] for r in rows],dtype=torch.float32,device='cuda:0');costs=torch.tensor([r['target_error'] for r in rows],dtype=torch.float32,device='cuda:0');scale=torch.tensor(SCALE,dtype=torch.float32,device='cuda:0')
                require_gpu(torch,features,costs,scale);_write_new(OUT/f'step-{index:04}.intent',_canonical({'step':index,'group_index':group_index}))
                optimizer.zero_grad(set_to_none=True);logits=model(features*scale).squeeze(-1);loss=listwise_loss(torch,logits,costs);loss.backward()
                gradients=[p.grad for p in model.parameters()]
                if any(g is None for g in gradients):raise RuntimeError('Missing gradient')
                require_gpu(torch,features,costs,logits,loss,*gradients)
                if any(not bool(torch.isfinite(g).all().item()) for g in gradients):raise RuntimeError('Nonfinite gradient')
                check_optimizer(torch,model,optimizer)
                if index==0:record_optimizer_start(authorization,_sha(config_raw))
                optimizer.step();check_optimizer(torch,model,optimizer)
                if any(not bool(torch.isfinite(p).all().item()) for p in model.parameters()):raise RuntimeError('Nonfinite model')
                torch.cuda.synchronize(torch.device('cuda:0'));_write_new(OUT/f'step-{index:04}.complete',_canonical({'step':index}));losses.append(float(loss.detach().item()))
            return ({'schema':'graphreader.axis-gpu-ranker-training-report.v1','status':'training_complete_pending_finalization','config_sha256':_sha(config_raw),'groups_sha256':GROUP_SHA,'target_sha256':TARGET_SHA,'steps':len(losses),'mean_loss':sum(losses)/len(losses),'device':'AMD Radeon RX 6600 XT','architecture':'gfx1032','cuda_index':0,'hip':str(torch.version.hip),'dev_read':False,'reserved_read':False,'production_approved':False},lambda:atomic_checkpoint(torch,model,_sha(config_raw)))
        return post_acquire(authorization,config_raw,len(order),operation,complete_training_candidate)
if __name__=='__main__':
    if len(sys.argv)!=1:raise SystemExit('usage: trainer_v3.py')
    print('report_sha256',run_training())
