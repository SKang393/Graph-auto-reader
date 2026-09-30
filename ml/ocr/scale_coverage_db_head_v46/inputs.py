# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Preserve V44 inputs and add pixel-derived, twice-size training windows."""
from __future__ import annotations

import argparse
from hashlib import sha256
import json
import math
from pathlib import Path
import time

import numpy as np
import torch
from torch.nn import functional as F

from ml.ocr.official_bakeoff import captured_head_features as capture_api
from ml.ocr.official_bakeoff import db_inverse_targets, frozen_head_training, frozen_trunk_head
from ml.markers.classifier.native_context_v3.runner import WorkBudget

CAPTURE = 'artifacts/goal22-runs/ocr-text-extent-tensors/report.json'
CAPTURE_SHA = '15e48b27ce3b8f5f53311abbef7235c35b3e8e361c8baddb09a4d3a7e7d5938e'
FEATURES = 'artifacts/goal22-runs/ocr-text-extent-features/report.json'
FEATURES_SHA = '8d0a2e3fe9f8b5a8abbf73ab3f487ed632d99e84c9b112dc8b718366d680edf0'
TRUTH = 'artifacts/goal22-runs/ocr-text-extent-preflight/train-text-truth.json'
TRUTH_SHA = '60a474a7e417c5f4b3622a7920da18d70d2b6c8677a644f13137d1c08662e1d3'
PARENT = 'artifacts/goal22-runs/pretrained-detector-head-feasibility/reviewed-parent.onnx'
TRUNK = 'artifacts/goal22-runs/ocr-text-extent-features/frozen-trunk.onnx'
TRUNK_SHA = '6115e9e02bb20ea495b04e3f5329721115ba72b5e79cc7246773919c9699b081'
INITIALIZER = 'artifacts/goal22-runs/ocr-v44-text-extent/training-run3/selected-head.pt'
INITIALIZER_SHA = '977145c75972ba5cdcabbd3b86dcf1fc3ab62916be65e0d7159933c80e2f34cf'
INITIAL_MODEL = 'artifacts/goal22-runs/ocr-v44-text-extent/candidate-run1/detector-text-extent-v44-p1.onnx'
INITIAL_MODEL_SHA = '615495cc882b6ae8633081d89c3ab08c0d4a80a04aa36bbde4157fc7828f5923'
PARITY_PROTOCOL = 'ml/ocr/official_bakeoff/SERVER_COMPARISON_PROTOCOL.json'
RECIPE = {'scale': 2, 'window_width': 1024, 'window_lefts': [0, 768, 1024],
    'interpolation': 'bilinear_normalized_bgr_align_corners_false',
    'partial_text': 'ignore_clipped_box_in_loss_preserve_full_positive_targets',
    'retain_all_original_train_and_dev_bytes': True}

def digest(path):
    return frozen_trunk_head.sha256_file(Path(path))

def reference(root, path):
    return {'path': Path(path).resolve().relative_to(root.resolve()).as_posix(), 'sha256': digest(path)}

def bound(root, descriptor):
    path = (root / descriptor['path']).resolve()
    if not path.is_relative_to(root.resolve()) or digest(path) != descriptor['sha256']:
        raise ValueError('Bound scale-coverage input changed or escaped the repository')
    return path

def read_bound(root, name, expected):
    return json.loads(bound(root, {'path': name, 'sha256': expected}).read_text(encoding='utf-8-sig'))

def parity_tolerance(root):
    """Reuse the existing full-model conversion tolerance, not an accuracy bar."""
    protocol=json.loads((root/PARITY_PROTOCOL).read_text(encoding='utf-8'))
    value=protocol['conversion']['maximum_absolute_difference']
    if value!=0.0001:
        raise ValueError('The referenced numerical conversion tolerance changed')
    return value

def immutable(values):
    values = np.ascontiguousarray(values, dtype=np.float32)
    if not np.isfinite(values).all():
        raise ValueError('Non-finite scale-coverage array')
    return np.frombuffer(values.tobytes(), dtype=np.float32).reshape(values.shape)

def array_ref(root, path, values):
    values = immutable(values)
    with path.open('xb') as stream:
        stream.write(values.tobytes())
    return {**reference(root, path), 'shape': list(values.shape), 'dtype': 'float32-le'}

def load_array(root, descriptor):
    if descriptor['dtype'] != 'float32-le':
        raise ValueError('Unsupported array encoding')
    raw = bound(root, descriptor).read_bytes()
    if len(raw) != math.prod(descriptor['shape']) * 4:
        raise ValueError('Array shape/byte-count mismatch')
    result = np.frombuffer(raw, dtype='<f4').reshape(descriptor['shape'])
    if not np.isfinite(result).all():
        raise ValueError('Non-finite stored array')
    return result

def project_training_boxes(capture, truth):
    if (truth.get('split') != 'train' or not truth.get('synthetic_only') or
        truth.get('private_data') is not False or truth.get('sealed_data') is not False):
        raise ValueError('Only owned training truth is allowed')
    train = [p for p in capture['panels'] if p['split'] == 'train']
    dev = [p for p in capture['panels'] if p['split'] == 'validation']
    if ({p['source_sha256'] for p in train} & {p['source_sha256'] for p in dev}):
        raise ValueError('Train/dev source overlap')
    boxes = {p['panel_id']: [] for p in train}
    seen = set()
    for row in truth['truths']:
        if row['truth_id'] in seen:
            raise ValueError('Repeated training truth')
        seen.add(row['truth_id'])
        a,b,c,d = row['source_box_ltrb']
        matches = []
        for panel in train:
            x,y,w,h = panel['crop']
            if (panel['source_sha256'] == row['source_sha256'] and
                x <= a < c <= x+w and y <= b < d <= y+h):
                th,tw = panel['tensor']['shape'][2:]
                matches.append((panel, [(a-x)*tw/w,(b-y)*th/h,(c-x)*tw/w,(d-y)*th/h]))
        if len(matches) != 1:
            raise ValueError('Training truth must retain exactly one full original projection')
        panel, box = matches[0]
        boxes[panel['panel_id']].append({'truth_id': row['truth_id'], 'box': box})
    return boxes

def augmented_geometry(boxes, left, height, width=1024):
    """Record every visible label; do not supervise cut words as complete words."""
    full, partial = [], []
    for row in boxes:
        a,b,c,d = row['box']
        box = [a*2-left,b*2,c*2-left,d*2]
        clipped = [max(0,box[0]),max(0,box[1]),min(width,box[2]),min(height,box[3])]
        if clipped[2] <= clipped[0] or clipped[3] <= clipped[1]:
            continue
        record = {'truth_id': row['truth_id'], 'box': clipped}
        (full if clipped == box else partial).append(record)
    return full, partial

def targets(width, height, full, partial):
    built = db_inverse_targets.build_inverse_targets(width,height,[r['box'] for r in full])
    mask = np.ones_like(built.target)
    for row in partial:
        a,b,c,d = row['box']
        mask[:,max(0,math.floor(b)):min(height,math.ceil(d)+1),
             max(0,math.floor(a)):min(width,math.ceil(c)+1)] = 0
    restored_positive_pixels = int(np.count_nonzero((mask==0)&(built.target>0)))
    mask[built.target>0] = 1
    return built.target, immutable(mask), restored_positive_pixels

def enlarge_tensor(tensor):
    if tensor.ndim != 4 or tensor.shape[:2] != (1,3) or tensor.dtype != np.float32:
        raise ValueError('Expected one normalized BGR tensor')
    with torch.inference_mode():
        result = F.interpolate(torch.from_numpy(np.array(tensor,copy=True)), scale_factor=2,
            mode='bilinear',align_corners=False).numpy()
    return immutable(result)

def prepare(root, output):
    import onnxruntime as ort
    root, output = root.resolve(), output.resolve()
    if not output.is_relative_to(root/'artifacts') or output.exists():
        raise ValueError('Use a new artifacts directory')
    output.mkdir(parents=True)
    start = time.perf_counter()
    budget = WorkBudget(output/'CANCEL')
    tolerance = parity_tolerance(root)
    torch.set_num_threads(12)
    torch.backends.mkldnn.enabled = False
    torch.use_deterministic_algorithms(True)
    with budget.work_block():
        capture, tensors = capture_api.load_capture_tensors(root/CAPTURE,CAPTURE_SHA)
        feature_report = read_bound(root,FEATURES,FEATURES_SHA)
        truth = read_bound(root,TRUTH,TRUTH_SHA)
        boxes = project_training_boxes(capture,truth)
        assert len(boxes)==28 and sum(map(len,boxes.values()))==709
        assert feature_report['capture_report_sha256']==CAPTURE_SHA
        features = {r['panel_id']:r for r in feature_report['panels']}
        parent = bound(root,{'path':PARENT,'sha256':frozen_trunk_head.REVIEWED_DETECTOR_SHA256})
        trunk_path = bound(root,{'path':TRUNK,'sha256':TRUNK_SHA})
        initializer = bound(root,{'path':INITIALIZER,'sha256':INITIALIZER_SHA})
        initial_model = bound(root,{'path':INITIAL_MODEL,'sha256':INITIAL_MODEL_SHA})
        bundle = frozen_trunk_head.extract_frozen_trunk_head(parent)
        buffers_before = frozen_head_training._frozen_bn_sha256(bundle.head)
        bundle.head.load_state_dict(torch.load(initializer,map_location='cpu',weights_only=True),strict=True)
        assert frozen_head_training._frozen_bn_sha256(bundle.head)==buffers_before
        bundle.head.eval()
        patch=frozen_trunk_head.patch_head_constants(parent,output/'initializer-reproduced.onnx',bundle.head)
        assert patch.output_sha256==INITIAL_MODEL_SHA
        options = ort.SessionOptions()
        options.log_severity_level=3
        options.intra_op_num_threads=options.inter_op_num_threads=1
        options.add_session_config_entry('session.intra_op.allow_spinning','0')
        options.add_session_config_entry('session.inter_op.allow_spinning','0')
        options.graph_optimization_level=ort.GraphOptimizationLevel.ORT_ENABLE_ALL
        trunk = ort.InferenceSession(str(trunk_path),options,providers=['CPUExecutionProvider'])
        full_model = ort.InferenceSession(str(initial_model),options,providers=['CPUExecutionProvider'])
    training, development, augmented_ids = [], [], set()
    old_target_inventory = sha256()
    for panel,tensor in tensors:
        with budget.work_block():
            ident=panel['panel_id']
            feature=features[ident]
            assert feature['input_sha256']==panel['tensor']['sha256']
            feature_path=Path(FEATURES).parent/feature['feature_file']
            feature_ref={'path':feature_path.as_posix(),'sha256':feature['feature_sha256'],
                'shape':feature['feature_shape'],'dtype':'float32-le'}
            load_array(root,feature_ref)
            input_ref={'path':(Path(CAPTURE).parent/panel['tensor']['file']).as_posix(),
                'sha256':panel['tensor']['sha256'],'shape':panel['tensor']['shape'],'dtype':'float32-le'}
            row={'panel_id':ident,'source_sha256':panel['source_sha256'],'split':panel['split'],
                'kind':'unchanged_original','input':input_ref,'features':feature_ref}
            if panel['split']=='validation':
                development.append(row)
                continue
            h,w=tensor.shape[2:]
            target,mask,_=targets(w,h,boxes[ident],[])
            old_target_inventory.update(ident.encode()+b'\0'+target.tobytes())
            row.update(target=array_ref(root,output/(ident+'.target.f32'),target),
                mask=array_ref(root,output/(ident+'.mask.f32'),mask),full_truth_count=len(boxes[ident]),partial_truth_count=0)
            training.append(row)
            enlarged=enlarge_tensor(tensor)
        for left in RECIPE['window_lefts']:
            with budget.work_block(),torch.inference_mode():
                new_id=f'{ident}-scale2-x{left}'
                values=np.ascontiguousarray(enlarged[:,:,:,left:left+1024])
                assert values.shape[3]==1024 and values.shape[2]<=1024
                full,partial=augmented_geometry(boxes[ident],left,values.shape[2])
                augmented_ids.update(r['truth_id'] for r in full)
                target,mask,restored=targets(1024,values.shape[2],full,partial)
                feature=trunk.run([frozen_trunk_head.FEATURE_OUTPUT_NAME],{'x':values})[0]
                expected=full_model.run(['fetch_name_0'],{'x':values})[0]
                actual=bundle.head(torch.from_numpy(np.ascontiguousarray(feature))).numpy()
                maximum=float(np.max(np.abs(expected-actual)))
                if expected.shape!=actual.shape or not np.isfinite(expected).all() or maximum>tolerance:
                    raise ValueError(f'New-window initializer parity failed: {new_id}, error={maximum}, tolerance={tolerance}')
                training.append({'panel_id':new_id,'source_sha256':panel['source_sha256'],'split':'train',
                    'kind':'scale2_window','parent_panel_id':ident,'scaled_tensor_left':left,
                    'input':array_ref(root,output/(new_id+'.input.f32'),values),
                    'features':array_ref(root,output/(new_id+'.features.f32'),feature),
                    'target':array_ref(root,output/(new_id+'.target.f32'),target),
                    'mask':array_ref(root,output/(new_id+'.mask.f32'),mask),
                    'full_truth_count':len(full),'partial_truth_count':len(partial),
                    'positive_pixels_retained_under_partial_overlap':restored,
                    'ignored_pixels':int(np.count_nonzero(mask==0)),
                    'initializer_parity_maximum_absolute_error':maximum,
                    'stricter_1e_5_passed':maximum<=1e-5,
                    'probability_0_3_pixel_decision_changes':int(np.count_nonzero((expected>=0.3)!=(actual>=0.3)))})
    assert len(training)==112 and len(development)==9
    assert old_target_inventory.hexdigest()=='1ed3f3f5e5f4633ebded60d16c48db9f6ae9dcc00da7a19c68be3e67baefbb19'
    assert not ({r['source_sha256'] for r in training}&{r['source_sha256'] for r in development})
    report={'schema':'graphreader.ocr-scale-coverage-inputs.v1','recipe':RECIPE,
        'training':training,'development':development,'original_train_truths':709,
        'original_train_target_inventory_sha256':old_target_inventory.hexdigest(),
        'augmented_truths_with_at_least_one_full_window':len(augmented_ids),
        'augmented_partial_truth_occurrences':sum(r['partial_truth_count'] for r in training),
        'initializer_parity_maximum_absolute_error':max(r.get('initializer_parity_maximum_absolute_error',0) for r in training),
        'parity_tolerance':tolerance,'initializer_reproduced_exact_onnx_bytes':True,
        'parent_refs':[reference(root,root/p) for p in (CAPTURE,FEATURES,TRUTH,PARENT,TRUNK,INITIALIZER,INITIAL_MODEL,PARITY_PROTOCOL)],
        'preparation_source':reference(root,Path(__file__)),
        'seconds':time.perf_counter()-start,'cpu':budget.report(),
        'optimizer_steps':0,'private_reads':0,'sealed_reads':0,'production_approved':False}
    (output/'manifest.json').write_text(json.dumps(report,indent=2,allow_nan=False)+'\n',encoding='utf-8')
    return report

def load_training(root, path, expected_sha):
    manifest=read_bound(root,path,expected_sha)
    if (manifest['schema']!='graphreader.ocr-scale-coverage-inputs.v1' or manifest['recipe']!=RECIPE or
        manifest['private_reads']!=0 or manifest['sealed_reads']!=0 or len(manifest['training'])!=112):
        raise ValueError('Training cache scope or recipe changed')
    dev_sources={r['source_sha256'] for r in manifest['development']}
    result=[]
    for row in manifest['training']:
        if row['split']!='train' or row['source_sha256'] in dev_sources:
            raise ValueError('Development data cannot enter optimization')
        result.append(frozen_head_training.TrainingPanel(row['panel_id'],'train',
            load_array(root,row['features']),load_array(root,row['target']),load_array(root,row['mask'])))
    return tuple(result),manifest

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    result=prepare(Path.cwd(),args.output)
    print(json.dumps({k:result[k] for k in ('seconds','original_train_truths',
        'augmented_truths_with_at_least_one_full_window','augmented_partial_truth_occurrences',
        'initializer_parity_maximum_absolute_error')}))
