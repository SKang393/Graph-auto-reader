# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Replay fixed synthetic-dev confidence and offset evidence without inference."""
from pathlib import Path
from hashlib import sha256
from collections import Counter
import json
import argparse
import numpy as np
from ml.markers.center.real_range_generator_v1.generator import build_split, ANTI_ALIAS_BLUR_RADII
from ml.markers.gate_seal import canonical_json_bytes, sha256_file

root = Path(__file__).resolve().parents[4]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', default='artifacts/goal22-runs/marker-v26-score-localization/reproducible-report.json')
args = parser.parse_args()
out = (root/args.output).resolve()
if not out.is_relative_to(root/'artifacts') or out.exists():
    raise ValueError('Use a new artifact output')
inputs = []
def read(path, digest):
    p = root/path
    if sha256_file(p) != digest: raise ValueError('Evidence identity changed: '+path)
    inputs.append({'path':path,'sha256':digest})
    return json.loads(p.read_text(encoding='utf-8'))

new = read('artifacts/goal22-runs/marker-v26-annulus/diagnosis-v1/diagnosis.json',
    '936a5ae1bca09bd431d94ad7c6acfc6cf521af78e8e45afea3498305a09250f8')
old = read('artifacts/goal22-runs/marker-v26-target-preflight/suppressor-anchor-diagnosis-v1/diagnosis.json',
    'ad923c8f9c658e4a51b4babf728f30f68e9bf33fb3d4528587b6f3e5998290db')
sources = {'ml/markers/center/real_range_generator_v1/generator.py':
    '2244094e8d8e3a8cacb83e11c5e7189ded46b25b1ce9ecdad1bd80e5dec78b41',
    'ml/markers/center/dataset.py':'95623cd99d54fbb374e5046c510a7307eeb89f72c10311d267eadd0f9304bc47'}
if any(sha256_file(root/p) != h for p,h in sources.items()): raise ValueError('Generator changed')
old_path = root/'artifacts/goal22-runs/marker-v26-target-preflight/suppressor-anchor-diagnosis-v1/fixed-v25-dev-outputs.npz'
new_path = root/'artifacts/goal22-runs/marker-v26-annulus/diagnosis-v1/fixed-v26-dev-outputs.npz'
if sha256_file(old_path) != old['cache']['sha256'] or sha256_file(new_path) != new['cache']['sha256']:
    raise ValueError('Prediction cache changed')
inputs.extend({'path':p.relative_to(root).as_posix(),'sha256':h} for p,h in
    ((old_path,old['cache']['sha256']),(new_path,new['cache']['sha256'])))
def array_sha(value):
    metadata = canonical_json_bytes({'dtype':str(value.dtype),'shape':list(value.shape)})
    return sha256(len(metadata).to_bytes(8,'little')+metadata+np.ascontiguousarray(value).tobytes()).hexdigest()
counts = Counter()
cohorts = {}
positive_scores, negative_scores, errors = [], [], []
scenes = build_split('dev', independent_layout=True)
with np.load(old_path,allow_pickle=False) as a, np.load(new_path,allow_pickle=False) as b:
    for scene_index, (scene, old_row, row) in enumerate(zip(scenes, old['component_dev']['cache_manifest'],new['component_dev']['cache_manifest'],strict=True)):
        identity = f'{scene.split}:{scene.family}:{scene.seed}'
        coordinates = a[old_row['proposal_coordinates_key']]
        values = b[row['v26_candidate_predictions_key']]
        if (coordinates.dtype != np.float32 or values.dtype != np.float32
                or coordinates.shape != (row['proposal_count'],2)
                or values.shape != (row['proposal_count'],4)
                or not np.isfinite(coordinates).all() or not np.isfinite(values).all()):
            raise ValueError('Cached array shape or values changed')
        if identity != row['scene_identity'] or identity != old_row['scene_identity']:
            raise ValueError('Scene mismatch')
        if array_sha(coordinates) != row['proposal_coordinates_sha256'] or array_sha(values) != row['v26_candidate_predictions_sha256']:
            raise ValueError('Cached array mismatch')
        centers = np.asarray(scene.centers,dtype=np.float32)
        distance = np.linalg.norm(coordinates[:,None,:]-centers[None,:,:],axis=2)
        assignment = distance.argmin(axis=1)
        positive = distance.min(axis=1) <= 3
        decoded = coordinates+values[:,1:3]*4
        error = np.linalg.norm(decoded-centers[assignment],axis=1)
        scores = values[:,0]
        positive_scores.extend(scores[positive].tolist())
        negative_scores.extend(scores[~positive].tolist())
        errors.extend(error[positive].tolist())
        counts['positive_anchors'] += int(positive.sum())
        counts['positive_anchors_below_threshold'] += int((positive & (scores<.25)).sum())
        counts['negative_anchors_above_threshold'] += int((~positive & (scores>=.25)).sum())
        counts['positive_anchors_decoded_beyond_five_pixels'] += int((positive & (error>5)).sum())
        for truth in range(len(centers)):
            owned = positive & (assignment==truth)
            counts['truths'] += 1
            cohort_keys = [f'diameter_px:{scene.diameters[truth]:g}',
                f'blur_radius:{ANTI_ALIAS_BLUR_RADII[scene_index % len(ANTI_ALIAS_BLUR_RADII)]:g}']
            for key in cohort_keys:
                cohort = cohorts.setdefault(key, Counter())
                cohort['truths'] += 1
                cohort['without_above_threshold_positive_anchor'] += int(not (scores[owned]>=.25).any())
                cohort['without_localized_above_threshold_positive_anchor'] += int(not ((scores[owned]>=.25)&(error[owned]<=5)).any())
            if not owned.any(): counts['truths_without_positive_anchor'] += 1
            elif not (scores[owned]>=.25).any(): counts['truths_without_above_threshold_positive_anchor'] += 1
            elif not ((scores[owned]>=.25)&(error[owned]<=5)).any(): counts['truths_without_localized_positive_anchor'] += 1
            else: counts['truths_with_localized_positive_anchor'] += 1
def summary(values):
    return {'count':len(values), **{name:float(np.quantile(values,q)) for name,q in [('p05',.05),('median',.5),('p95',.95)]}}
if len(scenes) != 167 or counts['truths'] != 2004 or len(positive_scores)+len(negative_scores) != 224840:
    raise ValueError('Component dev inventory changed')
if counts['truths'] != sum(counts[key] for key in (
        'truths_without_positive_anchor','truths_without_above_threshold_positive_anchor',
        'truths_without_localized_positive_anchor','truths_with_localized_positive_anchor')):
    raise ValueError('Truth partition does not conserve inventory')
if any(sha256_file(root/p) != h for p,h in sources.items()) or any(
        sha256_file(root/row['path']) != row['sha256'] for row in inputs):
    raise ValueError('Evidence changed during diagnosis')
report={'scope':'fixed-v26-component-synthetic-dev-cached-score-localization',
    'evidence_inputs':inputs,
    'diagnostic_source_sha256':sha256_file(Path(__file__)), 'sources':sources,
    'counts':dict(counts),'positive_scores':summary(positive_scores),'negative_scores':summary(negative_scores),
    'truth_cohorts':{key:dict(value) for key,value in sorted(cohorts.items())},
    'positive_anchor_decoded_error_px':summary(errors),'threshold':.25,
    'limitations':['Component dev only; family panels not included.','Before consensus and NMS; not final predictions or acceptance.'],
    'optimizer_steps':0,'model_inference':False,'private_reads':0,'sealed_reads':0,'production_approved':False}
out.parent.mkdir(parents=True,exist_ok=True)
with out.open('xb') as f: f.write(canonical_json_bytes(report))
print(json.dumps(report))
