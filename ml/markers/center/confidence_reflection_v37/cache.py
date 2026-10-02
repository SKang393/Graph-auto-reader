# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Admit fixed reflected features while retaining all original training rows."""
from __future__ import annotations

import json
from pathlib import Path

import numpy as np
import torch

from ml.markers.gate_seal import sha256_file
from ..confidence_spatial_v35 import cache as parent_cache
from .augmentation import TRANSFORMS


def load_training(repo: Path, report_path: Path, expected_sha256: str,
                  expected_rows: dict[str, int], expected_model_sha256: str, budget):
    if sha256_file(repo / report_path) != expected_sha256:
        raise ValueError('Reflection feature report changed')
    report = json.loads((repo / report_path).read_text(encoding='utf-8'))
    parent_rows = {key.removesuffix(':identity'): count for key, count in expected_rows.items()
                   if key.endswith(':identity')}
    expanded = {f'{scope}:{transform}': count for scope, count in parent_rows.items() for transform in TRANSFORMS}
    if (expanded != expected_rows or list(expanded) != list(expected_rows) or
            report.get('status') != 'fixed_reflection_training_features_measured' or
            report.get('transforms') != list(TRANSFORMS) or report.get('feature_width') != 768 or
            report.get('training_rows') != sum(expected_rows.values()) or
            report.get('original_training_rows') != sum(parent_rows.values()) or
            report.get('all_original_rows_and_labels_retained') is not True or
            report.get('model_parameters_and_buffers_unchanged') is not True or
            report.get('raw_reconstruction_max_absolute_error') != 0 or
            report.get('model', {}).get('sha256') != expected_model_sha256 or
            any(report.get(key) != 0 for key in ('optimizer_steps', 'private_reads', 'sealed_reads', 'development_tensor_reads'))):
        raise ValueError('Reflection population, identity or read scope changed')
    parent_ref = report['parent_feature_report']
    training, parent = parent_cache.load_training(repo, Path(parent_ref['path']), parent_ref['sha256'],
                                                  parent_rows, expected_model_sha256, budget)
    for item in [report['model'], *report['evidence']]:
        if sha256_file(repo / item['path']) != item['sha256']:
            raise ValueError('Reflection preparation evidence changed')
    groups = report.get('groups', [])
    if [group.get('scope') for group in groups] != list(expected_rows):
        raise ValueError('Reflection group membership or order changed')
    parent_groups = {group['scope']: group for group in parent['groups']}
    slices, offset = {}, 0
    for scope, count in parent_rows.items():
        slices[scope] = tuple(value[offset:offset+count] for value in training)
        offset += count
    pieces = [[], [], []]
    for group in groups:
        with budget.work_block():
            base_scope, transform = group['scope'].split(':')
            source = parent_groups[base_scope]
            count = source['rows']
            if (group.get('base_scope') != base_scope or group.get('transform') != transform or
                    group.get('rows') != count or group.get('positive') != source['positive'] or
                    group.get('negative') != source['negative'] or group.get('raw_reconstruction_max_absolute_error') != 0 or
                    any(group.get(name) != source[name] for name in ('tensors', 'labels', 'hard_negative'))):
                raise ValueError('Reflection row identity or labels changed')
            if transform == 'identity':
                if any(group.get(name) != source[name] for name in ('features', 'raw')):
                    raise ValueError('Original feature bytes changed')
                arrays = slices[base_scope]
            else:
                for name in ('features', 'raw'):
                    item = group[name]
                    if sha256_file(repo / item['path']) != item['sha256']:
                        raise ValueError('Reflected feature payload changed')
                features = np.load(repo / group['features']['path'], allow_pickle=False)
                raw = np.load(repo / group['raw']['path'], allow_pickle=False)
                if (features.shape != (count, 768) or features.dtype != np.float32 or
                        raw.shape != (count, 4) or raw.dtype != np.float32 or
                        not np.isfinite(features).all() or not np.isfinite(raw).all()):
                    raise ValueError('Reflected feature contract changed')
                arrays = (torch.from_numpy(features), *slices[base_scope][1:])
            for destination, value in zip(pieces, arrays, strict=True):
                destination.append(value)
    with budget.work_block():
        return tuple(torch.cat(values) for values in pieces), report
