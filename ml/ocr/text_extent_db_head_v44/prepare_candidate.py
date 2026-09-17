# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Prepare the unapproved synthetic candidate only after trained export parity."""
from __future__ import annotations
import argparse
from copy import deepcopy
import json
import math
from pathlib import Path
import shutil

from ml.ocr.text_extent_db_head_v44 import train_p1 as runner, verify_export
from ml.markers.gate_seal import sha256_file, canonical_json_bytes, verify_bound_source_snapshot

BASE = 'artifacts/goal22-runs/ocr-text-extent-baseline-candidate-v2/candidate.json'
BASE_SHA = '34f86cd6431b6cfdfc7ff10f85f5d7a859fc8e524dce557432afe93070efb361'
MODEL_ID = 'graph-text-extent-db-head-v44-p1'


def prepare(stage_path, stage_sha, parity_path, parity_sha, output_path, root=runner.REPOSITORY_ROOT):
    root = Path(root).resolve()
    stage_path = runner._bound(root, {'path': str(stage_path), 'sha256': stage_sha})
    parity_path = runner._bound(root, {'path': str(parity_path), 'sha256': parity_sha})
    stage, parity = runner._json(stage_path), runner._json(parity_path)
    config = runner._config(root)
    authorization = stage['training_authorization']
    if authorization['candidate_config_sha256'] != sha256_file(root/runner.CONFIG_PATH):
        raise ValueError('Candidate configuration differs from training')
    snapshot = (root/authorization['source_snapshot_path']).resolve()
    if not snapshot.is_relative_to(root):
        raise ValueError('Snapshot escaped repository')
    verify_bound_source_snapshot(root, snapshot, authorization['source_snapshot_sha256'])
    verify_export.validate_training_result(stage['training'])
    fixed_stage = {'schema': 'graphreader.ocr-text-extent-v44-training-stage.v1',
        'status': 'trained_pending_export_parity_and_dev', 'task': runner.TASK,
        'revision': runner.REVISION, 'candidate_id': 'P1', 'production_approved': False,
        'private_reads': 0, 'sealed_reads': 0}
    if any(type(stage.get(k)) is not type(v) or stage.get(k) != v for k,v in fixed_stage.items()):
        raise ValueError('Training is not an unapproved synthetic candidate')
    expected = {'schema': 'graphreader.ocr-text-extent-v44-trained-export-parity.v1',
                'stage': {'path': stage_path.relative_to(root).as_posix(), 'sha256': stage_sha},
                'source_sha256': sha256_file(Path(verify_export.__file__)),
                'model_sha256': stage['model_sha256'], 'checkpoint_sha256': stage['checkpoint_sha256'],
                'feature_inventory_sha256': stage['feature_inventory_sha256'],
                'panel_count': 37, 'tolerance': 1e-5, 'torch_threads': 12, 'onnx_threads': 1,
                'graph_optimization': 'ORT_ENABLE_ALL', 'optimizer_steps': 0,
                'private_reads': 0, 'sealed_reads': 0, 'production_approved': False, 'passed': True}
    if any(type(parity.get(k)) is not type(v) or parity.get(k) != v for k,v in expected.items()):
        raise ValueError('Trained parity identity or scope changed')
    capture = runner._json(runner._bound(root, config['bound_files']['capture_report']))
    expected_panels = {r['panel_id']: (r['split'], r['tensor']['sha256']) for r in capture['panels']}
    rows = parity.get('panels')
    if (not isinstance(rows,list) or len(rows) != 37
            or {r['panel_id']: (r['split'],r['input_sha256']) for r in rows} != expected_panels):
        raise ValueError('Parity omitted or changed a captured panel')
    errors = [r['maximum_absolute_error'] for r in rows]
    if (any(type(v) not in (int,float) or not math.isfinite(v) or v < 0 for v in errors)
            or max(errors) > 1e-5 or type(parity.get('maximum_absolute_error')) not in (int,float)
            or parity['maximum_absolute_error'] != max(errors)):
        raise ValueError('Trained numerical export parity failed')
    model = stage_path.parent/'detector-text-extent-v44-p1.onnx'
    if (sha256_file(model) != stage['model_sha256']
            or sha256_file(stage_path.parent/'selected-head.pt') != stage['checkpoint_sha256']):
        raise ValueError('Trained model or checkpoint changed')
    base = runner._json(runner._bound(root, {'path': BASE, 'sha256': BASE_SHA}))
    for descriptor in base['license_inputs'] + base['execution_assemblies']:
        runner._bound(root, {'path': descriptor['path'], 'sha256': descriptor['sha256']})
    runner._bound(root, {'path': base['native_path'], 'sha256': base['native_sha256']})
    for role in ('detector','recognizer'):
        row = base[role]
        runner._bound(root, {'path': row['model_path'], 'sha256': row['model_sha256']})
        runner._bound(root, {'path': row['manifest_path'], 'sha256': row['manifest_sha256']})
    output = (root/output_path).resolve()
    if not output.is_relative_to(root/'artifacts') or output.exists():
        raise ValueError('Use a new candidate artifact directory')
    output.mkdir(parents=True)
    target = output/model.name
    shutil.copyfile(model,target)
    if sha256_file(target) != stage['model_sha256']:
        raise ValueError('Model copy changed')
    manifest = runner._json(root/base['detector']['manifest_path'])
    manifest.update(model_id=MODEL_ID, model_version='0.0.1', sha256=stage['model_sha256'], files=[target.name])
    manifest['benchmarks'] = [{'scope': base['scope'], 'production_approved': False,
        'accuracy_status': 'pending-actual-application-evaluation', 'training_stage_sha256': stage_sha,
        'trained_export_parity_sha256': parity_sha, 'parent_model_sha256': runner.frozen_trunk_head.REVIEWED_DETECTOR_SHA256,
        'candidate_preparation_helper_sha256': sha256_file(Path(__file__))}]
    manifest_path = output/'detector.json'
    manifest_path.write_bytes(canonical_json_bytes(manifest))
    notice = output/'MODIFICATION-NOTICE.txt'
    notice.write_text('Derived from the Apache-2.0 PaddleOCR PP-OCRv5 mobile detector.\n'
        'Copyright 2026 Sungwoo Kang. Nine DB head constants trained on project-owned synthetic train data.\n'
        'Frozen trunk and batch-normalization statistics remain unchanged. Not production approved.\n'
        'Original notices and license remain in LICENSES/PaddlePaddle-PP-OCRv5-Models-Notice.txt and\n'
        'LICENSES/PaddlePaddle-PP-OCRv5-Models-Apache-2.0.txt.\n', encoding='utf-8')
    candidate = deepcopy(base)
    candidate['detector'] = {'model_path': target.relative_to(root).as_posix(), 'model_id': MODEL_ID,
        'model_version': '0.0.1', 'model_sha256': stage['model_sha256'],
        'manifest_path': manifest_path.relative_to(root).as_posix(), 'manifest_sha256': sha256_file(manifest_path)}
    candidate['license_inputs'].append({'path': notice.relative_to(root).as_posix(), 'sha256': sha256_file(notice)})
    candidate_path = output/'candidate.json'
    candidate_path.write_bytes(canonical_json_bytes(candidate))
    return {'path': candidate_path.relative_to(root).as_posix(), 'sha256': sha256_file(candidate_path)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('stage','stage-sha','parity','parity-sha','output'):
        parser.add_argument('--'+name, required=True)
    args = parser.parse_args()
    print(json.dumps(prepare(args.stage,args.stage_sha,args.parity,args.parity_sha,args.output)))


if __name__ == '__main__':
    main()
