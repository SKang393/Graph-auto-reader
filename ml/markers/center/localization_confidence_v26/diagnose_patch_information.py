# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Measure identical marker-input patches with conflicting synthetic labels."""
from __future__ import annotations

import argparse
from collections import Counter
from hashlib import sha256
import json
from pathlib import Path
import time

import numpy as np

from ml.markers.center.real_range_generator_v1.generator import build_split
from ml.markers.gate_seal import canonical_json_bytes, sha256_file

ROOT = Path(__file__).resolve().parents[4]
SNAPSHOT = 'ml/markers/training-seals/marker-center/marker-center-annulus-reservation-v26/P1/source-snapshot.json'
SNAPSHOT_SHA = '7ac89bec76bf7fed6ff08d2334a5a615128bc5047192a461b63757118f78080d'
# Imported by synthetic.dataset, but not called by this marker build_split.
# The unchanged marker generator only uses dataset.family_holdout_audit in
# its separate audit() function, which this diagnostic never invokes.
IMPORT_ONLY_RENDERER = 'c49c070afc505f92bfff7ff55b22e669c3ec2b76b56ff807b72a026ca0535283'


def run(output, source_sha):
    started = time.perf_counter()
    output = (ROOT/output).resolve()
    if not output.is_relative_to(ROOT/'artifacts') or output.exists():
        raise ValueError('Use a new artifact output')
    if sha256_file(Path(__file__)) != source_sha or sha256_file(ROOT/SNAPSHOT) != SNAPSHOT_SHA:
        raise ValueError('Diagnostic or historical snapshot changed')
    snapshot = json.loads((ROOT/SNAPSHOT).read_text(encoding='utf-8'))
    sources = [row for row in snapshot['sources'] if
        row['path'].startswith('ml/synthetic/') and row['path'].endswith('.py') or
        row['path'] in {'ml/markers/center/dataset.py',
            'ml/markers/center/real_range_generator_v1/generator.py',
            'ml/markers/center/mask_preserving_v24/mask_preserving.py',
            'ml/markers/center/focal_confidence_v21/focal_loss.py',
            'ml/markers/center/scale_classifier_v16/model.py'}]
    sources = [dict(row) for row in sources]
    for row in sources:
        if row['path'] == 'ml/synthetic/renderer.py':
            row['historical_sha256'] = row['sha256']
            row['sha256'] = IMPORT_ONLY_RENDERER
            row['execution_scope'] = 'imported transitive dependency; renderer functions not invoked'
    if len(sources) < 5 or any(sha256_file(ROOT/r['path']) != r['sha256'] for r in sources):
        raise ValueError('Historical input or objective source changed')
    # This is the existing 4-pixel proposal grid and 33-pixel zero-padded
    # window, restricted to anchors within eight pixels of a train truth.
    # Restriction limits diagnostic cost, not training or evaluation scope.
    scenes = build_split('train', independent_layout=True)
    counts = Counter()
    patches = {}
    for scene in scenes:
        tensor = scene.tensor.numpy()
        _, height, width = tensor.shape
        padded = np.pad(tensor, ((0,0),(16,16),(16,16)))
        yy, xx = np.mgrid[0:height:4, 0:width:4]
        coordinates = np.stack((xx.ravel(), yy.ravel()), axis=1)
        centers = np.asarray(scene.centers, dtype=np.float32)
        distances = ((coordinates[:,None,:]-centers[None,:,:])**2).sum(axis=2)
        nearest = distances.min(axis=1)
        for index in np.flatnonzero(nearest <= 64):
            x, y = coordinates[index]
            if tensor[0,max(0,y-8):min(height,y+9),max(0,x-8):min(width,x+9)].max() < .11:
                continue
            patch = np.ascontiguousarray(padded[:,y:y+33,x:x+33])
            if patch.shape != (3,33,33):
                raise ValueError('Proposal window changed')
            positive = bool(nearest[index] <= 9)
            counts['positive' if positive else 'negative'] += 1
            key = sha256(patch.tobytes()).hexdigest()
            row = patches.setdefault(key, [0,0])
            row[int(positive)] += 1
    mixed = [row for row in patches.values() if all(row)]
    if len(scenes) != 167 or sum(len(s.centers) for s in scenes) != 2004:
        raise ValueError('Synthetic train inventory changed')
    if sum(sum(row) for row in patches.values()) != sum(counts.values()):
        raise ValueError('Patch accounting failed')
    if sha256_file(Path(__file__)) != source_sha or any(sha256_file(ROOT/r['path']) != r['sha256'] for r in sources):
        raise ValueError('Source changed during diagnosis')
    report = {'schema': 'graphreader.marker-v26-train-patch-information.v1',
        'scope': 'synthetic-train-proposal-pool-within-eight-pixels-of-truth',
        'source_sha256': source_sha, 'historical_source_snapshot_sha256': SNAPSHOT_SHA,
        'authenticated_sources': sources, 'scene_count': len(scenes), 'truth_count': 2004,
        'patch_shape': [3,33,33], 'stride': 4, 'positive_distance_px': 3,
        'proposal_counts': dict(counts), 'unique_patch_count': len(patches),
        'identical_patch_groups_with_opposite_labels': len(mixed),
        'positive_rows_in_mixed_groups': sum(row[1] for row in mixed),
        'negative_rows_in_mixed_groups': sum(row[0] for row in mixed),
        'minimum_binary_errors_for_identical_inputs_in_this_pool': sum(min(row) for row in mixed),
        'limitations': ['Before negative sampling; not the selected training-row inventory.',
            'Only exact float32 equality is measured; near-identical windows are not counted.',
            'No model score, candidate comparison, threshold selection, or acceptance decision.'],
        'optimizer_steps': 0, 'model_inference': False, 'private_reads': 0,
        'sealed_reads': 0, 'production_approved': False,
        'elapsed_ms': (time.perf_counter()-started)*1000}
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open('xb') as stream:
        stream.write(canonical_json_bytes(report))
    return report


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', required=True)
    parser.add_argument('--source-sha', required=True)
    args = parser.parse_args()
    print(json.dumps(run(args.output, args.source_sha)))
