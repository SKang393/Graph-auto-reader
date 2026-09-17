# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Verify the trained checkpoint, patch containment, and all captured tensors."""
from __future__ import annotations

import argparse
from dataclasses import asdict
import json
import math
from pathlib import Path
import time

import numpy as np
import onnxruntime as ort
import torch

from ml.ocr.text_extent_db_head_v44 import train_p1 as runner
from ml.ocr.official_bakeoff import captured_head_features as capture_api
from ml.markers.gate_seal import sha256_file, verify_bound_source_snapshot, canonical_json_bytes


def validate_training_result(result):
    exact = {'optimizer_steps': 6720, 'epochs': 240, 'panel_count': 28,
             'positive_panel_count': 28, 'empty_positive_panels': 0,
             'intraop_threads': 12, 'seed': runner.RECIPE['seed'], 'batch_size': 1,
             'learning_rate': .0001, 'weight_decay': .0001, 'dice_epsilon': 1e-6,
             'torch_backend': runner.engine.TORCH_BACKEND,
             'trainable_parameter_names': list(runner.engine.TRAINABLE_PARAMETER_NAMES)}
    if any(type(result.get(k)) is not type(v) or result.get(k) != v for k,v in exact.items()):
        raise ValueError('Training result differs from the authorized recipe')
    rows = result.get('epoch_losses')
    if not isinstance(rows, list) or len(rows) != 240:
        raise ValueError('Training omitted epoch evidence')
    for epoch, row in enumerate(rows, 1):
        if (type(row.get('epoch')) is not int or row['epoch'] != epoch
                or type(row.get('optimizer_steps')) is not int or row['optimizer_steps'] != epoch*28
                or row.get('full_empty_negative_loss') is not None):
            raise ValueError('Epoch inventory changed')
        for key in ('full_train_loss','training_step_mean_loss','full_positive_dice_loss'):
            value = row.get(key)
            if type(value) not in (float,int) or not math.isfinite(value) or value < 0:
                raise ValueError('Epoch loss is not finite and nonnegative')
        if row['full_train_loss'] != row['full_positive_dice_loss']:
            raise ValueError('Positive-only training loss partition changed')
        runner.v39._sha(row.get('draw_order_sha256'), 'epoch draw order')
    selected = result.get('selected_epoch')
    if type(selected) is not int or selected != min(range(1,241), key=lambda i: rows[i-1]['full_train_loss']):
        raise ValueError('Checkpoint was not the earliest minimum train loss')
    if result.get('frozen_batch_norm_sha256_before') != result.get('frozen_batch_norm_sha256_after'):
        raise ValueError('Frozen normalization changed')


def verify(stage_path, stage_sha, output_path, source_sha, root=runner.REPOSITORY_ROOT):
    started = time.perf_counter()
    root = Path(root).resolve()
    stage_path, output = (root/stage_path).resolve(), (root/output_path).resolve()
    if (not stage_path.is_relative_to(root/'artifacts') or not output.is_relative_to(root/'artifacts')
            or output.exists() or sha256_file(stage_path) != stage_sha
            or sha256_file(Path(__file__)) != source_sha):
        raise ValueError('Export verification inputs or destination changed')
    runner._cpu_budget()
    stage = runner._json(stage_path)
    fixed = {'schema': 'graphreader.ocr-text-extent-v44-training-stage.v1',
             'status': 'trained_pending_export_parity_and_dev', 'task': runner.TASK,
             'revision': runner.REVISION, 'candidate_id': 'P1', 'private_reads': 0,
             'sealed_reads': 0, 'production_approved': False}
    if any(type(stage.get(k)) is not type(v) or stage.get(k) != v for k,v in fixed.items()):
        raise ValueError('Training stage is not the authorized completed experiment')
    prepared = runner.prepare_training(root)
    authorization = stage['training_authorization']
    expected = {'task': runner.TASK, 'revision': runner.REVISION, 'candidate_id': 'P1',
                'candidate_config_path': runner.CONFIG_PATH.as_posix(),
                'candidate_config_sha256': prepared.config_sha256,
                'runner_source_bundle_sha256': prepared.config['expected_runner_source_bundle_sha256'],
                'runner_source_paths': sorted(p.as_posix() for p in runner.RUNNER_SOURCE_PATHS)}
    if any(authorization.get(k) != v for k,v in expected.items()):
        raise ValueError('Training authorization changed')
    snapshot = (root/authorization['source_snapshot_path']).resolve()
    if not snapshot.is_relative_to(root):
        raise ValueError('Source snapshot escaped repository')
    verify_bound_source_snapshot(root, snapshot, authorization['source_snapshot_sha256'])
    if (stage['feature_inventory_sha256'] != prepared.feature_inventory_sha256
            or stage['target_inventory_sha256'] != prepared.target_inventory_sha256):
        raise ValueError('Training input inventory changed')
    validate_training_result(stage['training'])
    files = prepared.config['bound_files']
    parent = runner._bound(root, files['parent_model'])
    checkpoint = stage_path.parent/'selected-head.pt'
    model = stage_path.parent/'detector-text-extent-v44-p1.onnx'
    if sha256_file(checkpoint) != stage['checkpoint_sha256'] or sha256_file(model) != stage['model_sha256']:
        raise ValueError('Trained artifacts changed')
    bundle = runner.frozen_trunk_head.extract_frozen_trunk_head(parent)
    bundle.head.load_state_dict(torch.load(checkpoint, map_location='cpu', weights_only=True), strict=True)
    bundle.head.eval()
    if (runner.frozen_head_training._trainable_state_sha256(bundle.head)
            != stage['training']['selected_trainable_state_sha256']
            or runner.frozen_head_training._frozen_bn_sha256(bundle.head)
            != stage['training']['frozen_batch_norm_sha256_after']):
        raise ValueError('Checkpoint does not match selected training state')
    output.mkdir(parents=True)
    patch = runner.frozen_trunk_head.patch_head_constants(parent, output/'reproduced.onnx', bundle.head)
    runner.v40._validate_patch(patch, model)
    if patch.output_sha256 != stage['model_sha256']:
        raise ValueError('Checkpoint patch does not reproduce trained ONNX bytes')
    capture_path = runner._bound(root, files['capture_report'])
    capture, tensors = capture_api.load_capture_tensors(capture_path, files['capture_report']['sha256'])
    features_path = runner._bound(root, files['parity_report'])
    features, feature_sha, _ = runner.v39._validate_parity_and_features(
        runner._json(features_path), features_path.parent, capture, files['capture_report']['sha256'],
        sha256_file(root/'ml/ocr/official_bakeoff/captured_head_features.py'),
        sha256_file(root/'ml/ocr/official_bakeoff/frozen_trunk_head.py'), files['feature_model']['sha256'])
    if feature_sha != prepared.feature_inventory_sha256:
        raise ValueError('Parity feature inventory changed')
    options = ort.SessionOptions()
    options.intra_op_num_threads = options.inter_op_num_threads = 1
    options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    session = ort.InferenceSession(str(model), options, providers=['CPUExecutionProvider'])
    previous = (torch.get_num_threads(), torch.are_deterministic_algorithms_enabled(),
                torch.is_deterministic_algorithms_warn_only_enabled())
    rows = []
    try:
        torch.set_num_threads(12)
        torch.use_deterministic_algorithms(True)
        with torch.inference_mode(), torch.backends.mkldnn.flags(enabled=False):
            for panel, values in tensors:
                runner._cpu_budget()
                cached, input_sha = features[panel['panel_id']]
                if input_sha != panel['tensor']['sha256']:
                    raise ValueError('Cached feature and detector tensor differ')
                expected_output = bundle.head(torch.from_numpy(cached.copy())).numpy()
                actual = session.run([runner.frozen_trunk_head.MODEL_OUTPUT_NAME],
                    {runner.frozen_trunk_head.MODEL_INPUT_NAME: values})[0]
                if (actual.shape != expected_output.shape or not np.isfinite(actual).all()
                        or not np.isfinite(expected_output).all()):
                    raise ValueError('Trained parity output is invalid')
                rows.append({'panel_id': panel['panel_id'], 'split': panel['split'],
                    'input_sha256': input_sha, 'maximum_absolute_error': float(np.max(np.abs(actual-expected_output)))})
    finally:
        torch.set_num_threads(previous[0])
        torch.use_deterministic_algorithms(previous[1], warn_only=previous[2])
    verify_bound_source_snapshot(root, snapshot, authorization['source_snapshot_sha256'])
    if (sha256_file(stage_path) != stage_sha or sha256_file(Path(__file__)) != source_sha
            or sha256_file(checkpoint) != stage['checkpoint_sha256'] or sha256_file(model) != stage['model_sha256']):
        raise ValueError('Verification evidence changed during execution')
    maximum = max(row['maximum_absolute_error'] for row in rows)
    report = {'schema': 'graphreader.ocr-text-extent-v44-trained-export-parity.v1',
              'stage': {'path': stage_path.relative_to(root).as_posix(), 'sha256': stage_sha},
              'source_sha256': source_sha, 'model_sha256': stage['model_sha256'],
              'checkpoint_sha256': stage['checkpoint_sha256'], 'patch': asdict(patch),
              'feature_inventory_sha256': feature_sha, 'panel_count': len(rows), 'panels': rows,
              'maximum_absolute_error': maximum, 'tolerance': 1e-5, 'passed': maximum <= 1e-5,
              'torch_threads': 12, 'onnx_threads': 1, 'graph_optimization': 'ORT_ENABLE_ALL',
              'optimizer_steps': 0, 'private_reads': 0, 'sealed_reads': 0, 'production_approved': False,
              'elapsed_ms': (time.perf_counter()-started)*1000}
    (output/'report.json').write_bytes(canonical_json_bytes(report))
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('stage','stage-sha','output','source-sha'):
        parser.add_argument('--'+name, required=True)
    args = parser.parse_args()
    report = verify(args.stage, args.stage_sha, args.output, args.source_sha)
    print(json.dumps({key: report[key] for key in ('passed','maximum_absolute_error','panel_count','elapsed_ms')}))
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
