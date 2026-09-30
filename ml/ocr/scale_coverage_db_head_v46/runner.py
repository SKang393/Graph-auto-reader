# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""One authorized train-only V44 adaptation and complete tensor export parity."""
from __future__ import annotations

import argparse
from dataclasses import asdict
import json
from pathlib import Path
import time
import traceback

import numpy as np
import onnx
import onnxruntime as ort
import torch

from ml.markers.gate_seal import source_bundle_sha256, verify_bound_source_snapshot
from ml.markers.training_budget import acquire_training_candidate, void_candidate
from ml.markers.classifier.native_context_v3.runner import WorkBudget
from ml.ocr.official_bakeoff import frozen_trunk_head, frozen_head_training
from . import inputs, training

TASK='ocr-detection'
REVISION='graph-scale-coverage-db-head-v46'
CONFIG_PATH=Path('ml/ocr/scale_coverage_db_head_v46/p1.json')
RUNNER_SOURCES=tuple(Path(p) for p in (
    'ml/ocr/scale_coverage_db_head_v46/__init__.py',
    'ml/ocr/scale_coverage_db_head_v46/inputs.py',
    'ml/ocr/scale_coverage_db_head_v46/training.py',
    'ml/ocr/scale_coverage_db_head_v46/runner.py',
    'ml/ocr/scale_coverage_db_head_v46/protocol.json',
    'ml/ocr/official_bakeoff/frozen_trunk_head.py',
    'ml/ocr/official_bakeoff/frozen_head_training.py',
    'ml/ocr/official_bakeoff/captured_head_features.py',
    'ml/ocr/official_bakeoff/db_inverse_targets.py',
    'ml/ocr/official_bakeoff/SERVER_COMPARISON_PROTOCOL.json',
    'ml/markers/classifier/native_context_v3/runner.py',
    'ml/training_cpu_budget.py','tools/Run-TrainingCpuBudget.ps1',
    'ml/markers/training_budget.py','ml/markers/gate_seal.py',
    'ml/policy/evidence_policy.py','ml/policy/evidence-policy.json','ml/policy/acceptance-bars.json'))

def validate_config(config):
    if (config.get('task'),config.get('revision'),config.get('candidate_id'))!=(TASK,REVISION,'P1'):
        raise ValueError('Training identity changed')
    if (config.get('recipe')!=training.RECIPE or config.get('augmentation')!=inputs.RECIPE or
        config.get('sealed_runs_authorized')!=0 or config.get('private_reads')!=0):
        raise ValueError('Registered recipe or corpus authorization changed')
    if config.get('initializer_sha256')!=inputs.INITIALIZER_SHA:
        raise ValueError('V44 initializer identity changed')

def export_parity(root,model,head,manifest,budget):
    tolerance=inputs.parity_tolerance(root)
    options=ort.SessionOptions()
    options.log_severity_level=3
    options.intra_op_num_threads=options.inter_op_num_threads=1
    options.add_session_config_entry('session.intra_op.allow_spinning','0')
    options.add_session_config_entry('session.inter_op.allow_spinning','0')
    options.graph_optimization_level=ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    session=ort.InferenceSession(str(model),options,providers=['CPUExecutionProvider'])
    rows=[]
    with torch.inference_mode():
        for row in (*manifest['training'],*manifest['development']):
            with budget.work_block():
                pixels=inputs.load_array(root,row['input'])
                features=inputs.load_array(root,row['features'])
                actual=session.run(['fetch_name_0'],{'x':np.ascontiguousarray(pixels)})[0]
                expected=head(torch.from_numpy(np.array(features,copy=True))).numpy()
                if actual.shape!=expected.shape or not np.isfinite(actual).all():
                    raise ValueError('Export tensor shape/finite contract failed')
                maximum=float(np.max(np.abs(actual-expected)))
                rows.append({'panel_id':row['panel_id'],'split':row['split'],
                    'input_sha256':row['input']['sha256'],'maximum_absolute_error':maximum,
                    'probability_0_3_pixel_decision_changes':int(np.count_nonzero((actual>=0.3)!=(expected>=0.3)))})
                if maximum>tolerance:
                    raise ValueError(f'Trained export parity exceeded existing conversion tolerance: {maximum}')
    return {'panels':rows,'maximum_absolute_error':max(r['maximum_absolute_error'] for r in rows),
        'tolerance':tolerance,'passed':True,'stricter_1e_5_passed':all(r['maximum_absolute_error']<=1e-5 for r in rows),
        'tolerance_source':inputs.reference(root,root/inputs.PARITY_PROTOCOL),
        'provider':'CPUExecutionProvider','graph_optimization':'ORT_ENABLE_ALL',
        'recognition_or_detection_accuracy_claimed':False}

def run(root,output,*,resume=None,resume_sha256=None):
    root,output=root.resolve(),output.resolve()
    if not output.is_relative_to(root/'artifacts') or output.exists():
        raise ValueError('Use a new artifacts output directory')
    config=json.loads((root/CONFIG_PATH).read_text(encoding='utf-8'))
    validate_config(config)
    if source_bundle_sha256(root,RUNNER_SOURCES)!=config['expected_runner_source_bundle_sha256']:
        raise ValueError('Registered runner changed')
    if (resume is None)!=(resume_sha256 is None):
        raise ValueError('Recovery requires its exact checksum')
    if resume is not None:
        resume=inputs.bound(root,{'path':str(resume),'sha256':resume_sha256})
    authorization=acquire_training_candidate(root,task=TASK,revision=REVISION,
        candidate_id='P1',config_path=CONFIG_PATH,runner_source_paths=RUNNER_SOURCES)
    started=time.perf_counter()
    try:
        output.mkdir(parents=True)
        budget=WorkBudget(output/'CANCEL')
        torch.set_num_threads(12)
        torch.backends.mkldnn.enabled=False
        torch.use_deterministic_algorithms(True)
        with budget.work_block():
            panels,manifest=inputs.load_training(root,config['cache_path'],config['cache_sha256'])
            if len(panels)!=config['training_panels']:
                raise ValueError('Training panel denominator changed')
            parent=inputs.bound(root,{'path':inputs.PARENT,'sha256':frozen_trunk_head.REVIEWED_DETECTOR_SHA256})
            initializer=inputs.bound(root,{'path':inputs.INITIALIZER,'sha256':inputs.INITIALIZER_SHA})
            bundle=frozen_trunk_head.extract_frozen_trunk_head(parent)
            frozen=frozen_head_training._frozen_bn_sha256(bundle.head)
            bundle.head.load_state_dict(torch.load(initializer,map_location='cpu',weights_only=True),strict=True)
            if frozen_head_training._frozen_bn_sha256(bundle.head)!=frozen:
                raise ValueError('Initializer changed frozen batch normalization')
        binding={'candidate_config_sha256':inputs.digest(root/CONFIG_PATH),
            'cache_manifest_sha256':config['cache_sha256'],'initializer_sha256':inputs.INITIALIZER_SHA}
        trained=training.train(bundle.head,panels,training.RECIPE,output,budget,binding,resume=resume)
        del panels
        assert trained['optimizer_steps']==config['optimizer_steps_expected']
        with budget.work_block():
            torch.save(bundle.head.state_dict(),output/'selected-head.pt')
            model=output/'detector-scale-coverage-v46-p1.onnx'
            patch=frozen_trunk_head.patch_head_constants(parent,model,bundle.head)
            onnx.checker.check_model(onnx.load(model))
            assert set(patch.changed_constants)<=set(frozen_trunk_head.TRAINABLE_CONSTANT_SHAPES)
        training.atomic_json(output/'progress.json',{'status':'export_parity'})
        parity=export_parity(root,model,bundle.head,manifest,budget)
        training.atomic_json(output/'parity.json',parity)
        verify_bound_source_snapshot(root,authorization.snapshot_path,authorization.binding['source_snapshot_sha256'])
        report={'schema':'graphreader.ocr-scale-coverage-v46-training.v1',
            'status':'trained_parity_pass_pending_application_dev','task':TASK,'revision':REVISION,'candidate_id':'P1',
            'binding':authorization.binding,'training':trained,'patch':asdict(patch),
            'checkpoint':inputs.reference(root,output/'selected-head.pt'),'model':inputs.reference(root,model),
            'parity':inputs.reference(root,output/'parity.json'),'cache_sha256':config['cache_sha256'],
            'seconds':time.perf_counter()-started,'cpu':budget.report(),
            'private_reads':0,'sealed_reads':0,'production_approved':False,'packaged_build_created':False}
        training.atomic_json(output/'training-stage.json',report)
        return report
    except BaseException as error:
        if output.is_dir():
            (output/'exception.txt').write_text(traceback.format_exc(),encoding='utf-8')
        void_candidate(authorization,error)
        raise

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--resume',type=Path)
    parser.add_argument('--resume-sha256')
    args=parser.parse_args()
    report=run(Path.cwd(),args.output,resume=args.resume,resume_sha256=args.resume_sha256)
    print(json.dumps({'status':report['status'],'seconds':report['seconds']}))
