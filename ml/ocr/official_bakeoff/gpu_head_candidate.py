# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Fixed synthetic marker-style GPU candidate; no execution without ledger authorization."""
from __future__ import annotations
import argparse
from dataclasses import asdict
from hashlib import sha256
import json
import os
from pathlib import Path
import time
import numpy as np
import onnxruntime as ort
import torch
from ml.markers import training_budget
from ml.markers.gate_seal import canonical_json_bytes, sha256_file, source_bundle_sha256, verify_bound_source_snapshot
from ml.ocr.legend_coverage_db_head_v45 import train_p1 as v45
from ml.ocr.pretrained_db_head_v39 import train_p1 as v39
from ml.ocr.inverse_db_target_v40 import train_p1 as v40
from . import gpu_head_bridge as bridge, gpu_head_training as gpu, frozen_trunk_head as trunk
from . import marker_style_head_inputs as marker

TASK = 'ocr-detection'
REVISION = 'marker_style_coverage_gpu_head_v1'
CANDIDATE = 'P1'
CONFIG = Path('ml/ocr/official_bakeoff/marker_style_gpu_candidate.json')
SOURCES = tuple(dict.fromkeys((*v45.RUNNER_SOURCE_PATHS, *map(Path, (
    'ml/ocr/official_bakeoff/gpu_head_candidate.py',
    'ml/ocr/official_bakeoff/gpu_head_training.py',
    'ml/ocr/official_bakeoff/gpu_head_bridge.py',
    'ml/ocr/official_bakeoff/marker_style_head_inputs.py',
    'ml/ocr/official_bakeoff/train_marker_style_coverage.py',
    'ml/policy/evidence-policy.json', 'ml/policy/acceptance-bars.json')))))


def _config(root: Path, expected_hash: str):
    path = root / CONFIG
    if sha256_file(path) != expected_hash:
        raise ValueError('Explicit candidate configuration hash changed')
    config = json.loads(path.read_text(encoding='utf-8'))
    if (config.get('task'), config.get('revision'), config.get('candidate_id')) != (TASK, REVISION, CANDIDATE):
        raise ValueError('Candidate identity changed')
    if config.get('expected_runner_source_bundle_sha256') != source_bundle_sha256(root, SOURCES):
        raise ValueError('Candidate source bundle changed')
    return config


def _bound(root, descriptor):
    path = (root / descriptor['path']).resolve()
    if not path.is_relative_to(root.resolve()) or sha256_file(path) != descriptor['sha256']:
        raise ValueError('Bound candidate input changed or escaped its source root')
    return path


def _persist_training_result(output, head, result, authorization, inputs, elapsed_seconds):
    """Atomically retain selected bytes and the completed optimization record."""
    checkpoint = output / 'selected-head.pt'
    record = output / 'training-result.json'
    checkpoint_temp = output / 'selected-head.pending.pt'
    record_temp = output / 'training-result.pending.json'
    if any(path.exists() for path in (checkpoint, record, checkpoint_temp, record_temp)):
        raise ValueError('Training result destination already exists')
    v39._write_checkpoint(checkpoint_temp, head)
    checkpoint_hash = sha256_file(checkpoint_temp)
    with checkpoint_temp.open('rb+') as stream:
        os.fsync(stream.fileno())
    os.replace(checkpoint_temp, checkpoint)
    payload = {'schema':'graphreader.marker-style-gpu-training-result.v1',
        'status':'optimization_complete_pending_export', 'task':TASK, 'revision':REVISION,
        'candidate_id':CANDIDATE, 'authorization':authorization, 'inputs':inputs,
        'training':asdict(result), 'checkpoint_sha256':checkpoint_hash,
        'elapsed_seconds':elapsed_seconds, 'private_reads':0, 'sealed_reads':0,
        'production_approved':False}
    with record_temp.open('xb') as stream:
        stream.write(canonical_json_bytes(payload))
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(record_temp, record)
    return checkpoint

def prepare(config, historical_root, input_root, old_prefix, relocated_root):
    historical_root, input_root = historical_root.resolve(), input_root.resolve()
    if sha256_file(historical_root / v45.CONFIG_PATH) != config['historical_config_sha256']:
        raise ValueError('Historical candidate configuration changed')
    for descriptor in config['historical_bound_files'].values():
        _bound(historical_root, descriptor)
    files = config['new_inputs']
    request, capture, manifest = (_bound(input_root, files[k]) for k in ('request', 'capture', 'targets'))
    model = _bound(historical_root, config['historical_bound_files']['parent_model'])
    manifest_json = json.loads(manifest.read_text(encoding='utf-8'))
    if (manifest_json.get('schema') != 'graphreader.marker-style-model-grid-inputs.v1'
        or manifest_json.get('split') != 'train' or manifest_json.get('source_count') != 9
        or manifest_json.get('panel_count') != 9 or manifest_json.get('truth_count') != 224
        or manifest_json.get('private_data') is not False or manifest_json.get('sealed_data') is not False):
        raise ValueError('New target inventory or scope changed')
    head, panels, evidence = bridge.prepare_combined(historical_root=historical_root, old_prefix=old_prefix,
        relocated_root=relocated_root, repository_root=input_root, model_path=model, request_path=request,
        request_sha256=files['request']['sha256'], report_path=capture, report_sha256=files['capture']['sha256'])
    historical_capture = json.loads(_bound(historical_root, config['historical_bound_files']['capture_report']).read_text(encoding='utf-8'))
    supplemental_capture = json.loads(_bound(historical_root, config['historical_bound_files']['supplemental_capture_report']).read_text(encoding='utf-8'))
    historical_rows = historical_capture['panels'] + supplemental_capture['panels']
    old_train_sources = {row['source_sha256'] for row in historical_rows if row['split'] == 'train'}
    old_dev_sources = {row['source_sha256'] for row in historical_rows if row['split'] == 'validation'}
    new_sources = {row['source_sha256'] for row in manifest_json['panels']}
    if len(old_train_sources) != 26 or len(old_dev_sources) != 3 or len(new_sources) != 9:
        raise ValueError('Fixed train or open-dev source inventory changed')
    if old_train_sources & old_dev_sources or new_sources & (old_train_sources | old_dev_sources):
        raise ValueError('Training and historical source populations overlap')
    rows = {row['panel_id']: row for row in manifest_json['panels']}
    if len(rows) != 9 or set(rows) != {panel.panel_id for panel in panels[34:]}:
        raise ValueError('New target panel identities changed')
    for panel in panels[34:]:
        row = rows[panel.panel_id]
        for label, array in (('target', panel.target), ('mask', panel.mask)):
            _bound(input_root, row[label])
            if sha256(array.tobytes()).hexdigest() != row[label]['sha256']:
                raise ValueError('Rebuilt supervision differs from bound target bytes')
    return head, panels, evidence, model


def run(root, config_hash, historical_root, input_root, old_prefix, relocated_root, output):
    root = root.resolve()
    input_root = input_root.resolve()
    config = _config(root, config_hash)
    output = output.resolve()
    if not output.is_relative_to(root / 'artifacts') or output.exists():
        raise ValueError('Output must be a new directory inside this worktree artifacts')
    auth = training_budget.acquire_training_candidate(root, task=TASK, revision=REVISION,
        candidate_id=CANDIDATE, config_path=CONFIG, runner_source_paths=SOURCES)
    started = time.perf_counter()
    phase = 'preflight'
    try:
        head, panels, inputs, parent = prepare(config, historical_root, input_root, old_prefix, relocated_root)
        if auth.binding['candidate_config_sha256'] != config_hash:
            raise ValueError('Authorized candidate configuration changed')
        verify_bound_source_snapshot(root, auth.snapshot_path, auth.binding['source_snapshot_sha256'])
        output.mkdir(parents=True, exist_ok=False)
        phase = 'optimization'
        result = gpu.train_extended_head_gpu(head, panels)
        if result.epochs != 240 or result.optimizer_steps != 43*240:
            raise ValueError('Fixed optimizer schedule changed')
        checkpoint = _persist_training_result(output, head, result, auth.binding, inputs, time.perf_counter()-started)
        phase = 'export'
        model = output/'candidate.onnx'
        patch = trunk.patch_head_constants(parent, model, head)
        v40._validate_patch(patch, model)
        # Validate actual new train tensors against the trained GPU head; no dev selection.
        split = marker.load_model_grid_inputs(input_root/config['new_inputs']['request']['path'],
            config['new_inputs']['request']['sha256'], input_root/config['new_inputs']['capture']['path'],
            config['new_inputs']['capture']['sha256'], repository_root=input_root)
        by_id = {p.panel_id: p for p in panels[34:]}
        session = ort.InferenceSession(str(model), providers=['CPUExecutionProvider'])
        errors = []
        with torch.inference_mode():
            for panel in split.panels:
                actual = session.run([trunk.MODEL_OUTPUT_NAME], {trunk.MODEL_INPUT_NAME: panel.input_values})[0]
                expected = head(torch.tensor(by_id[panel.panel_id].features, device=gpu.DEVICE)).cpu().numpy()
                if actual.shape != expected.shape or not np.isfinite(actual).all() or not np.isfinite(expected).all():
                    raise ValueError('Trained ONNX parity output invalid')
                errors.append(float(np.max(np.abs(actual-expected))))
        if len(errors) != 9 or max(errors) > 1e-5:
            raise ValueError('Trained ONNX parity exceeded fixed tolerance')
        verify_bound_source_snapshot(root, auth.snapshot_path, auth.binding['source_snapshot_sha256'])
        report = {'schema':'graphreader.marker-style-gpu-training-stage.v1', 'status':'trained_pending_open_dev',
            'task':TASK, 'revision':REVISION, 'candidate_id':CANDIDATE, 'authorization':auth.binding,
            'inputs':inputs, 'training':asdict(result), 'patch':asdict(patch),
            'checkpoint_sha256':sha256_file(checkpoint), 'model_sha256':sha256_file(model),
            'trained_onnx_parity_maximum_absolute_error':max(errors), 'elapsed_seconds':time.perf_counter()-started,
            'private_reads':0, 'sealed_reads':0, 'production_approved':False}
        stage = output/'training-stage.json'
        stage.write_bytes(canonical_json_bytes(report))
        return report
    except BaseException as error:
        training_budget.void_candidate(auth, error)
        raise


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for option in ('config-sha256','historical-root','input-root','old-prefix','relocated-root','output'):
        parser.add_argument('--'+option, required=True)
    args=parser.parse_args()
    report=run(Path.cwd(), args.config_sha256, Path(args.historical_root), Path(args.input_root),
               args.old_prefix, Path(args.relocated_root), Path(args.output))
    print(json.dumps({'status': report['status'], 'model_sha256': report['model_sha256']}))


if __name__ == '__main__':
    main()
