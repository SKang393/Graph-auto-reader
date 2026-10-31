# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Provisional, truth-separated group validation; not an admission protocol."""
from dataclasses import dataclass
from hashlib import sha256
import json
import math
import random
import re

from ml.axis.candidate_ranker_foundation_v1.features import FEATURE_NAMES, MAX_CANDIDATES

_HASH = re.compile(r'^[0-9a-f]{64}$')
MAX_GROUPS = 4096
MAX_GROUP_BYTES = 64 * 1024 * 1024

def _hash(value):
    if not isinstance(value, str) or not _HASH.fullmatch(value):
        raise ValueError('Expected lowercase SHA-256')
    return value

def _number(value):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        raise ValueError('Expected finite number')
    return float(value)

@dataclass(frozen=True)
class Recipe:
    seed: int
    epochs: int
    learning_rate: float
    group_bytes_sha256: str

    def validate(self):
        if type(self.seed) is not int or not 0 <= self.seed < 2**31:
            raise ValueError('Invalid seed')
        if type(self.epochs) is not int or not 1 <= self.epochs <= 20:
            raise ValueError('Invalid epoch count')
        if type(self.learning_rate) is not float or not 0 < _number(self.learning_rate) <= 0.01:
            raise ValueError('Invalid learning rate')
        _hash(self.group_bytes_sha256)

def parse_groups(raw, recipe, authorized_panels):
    """Validate source/panel membership and exact bytes without opening paths.

    authorized_panels is supplied by a future authenticated train-only
    admission, not by the file or its claims. This function does not admit a run.
    """
    if type(recipe) is not Recipe: raise ValueError('Exact recipe required')
    recipe.validate()
    if type(raw) is not bytes or len(raw) > MAX_GROUP_BYTES or sha256(raw).hexdigest() != recipe.group_bytes_sha256:
        raise ValueError('Group input digest differs')
    if type(authorized_panels) is not frozenset or not authorized_panels:
        raise ValueError('Independent panel inventory required')
    for entry in authorized_panels:
        if type(entry) is not tuple or len(entry) != 2: raise ValueError('Invalid panel inventory')
        _hash(entry[0]); _hash(entry[1])
    def reject_constant(_): raise ValueError('Nonfinite JSON number')
    def unique_object(pairs):
        result = {}
        for key, value in pairs:
            if key in result: raise ValueError('Duplicate JSON key')
            result[key] = value
        return result
    doc = json.loads(raw, parse_constant=reject_constant, object_pairs_hook=unique_object)
    if not isinstance(doc, dict) or set(doc) != {'schema','split','feature_names','groups'} or doc['schema'] != 'graphreader.axis-ranker-training-groups.draft-v1' or doc['split'] != 'train' or doc['feature_names'] != list(FEATURE_NAMES):
        raise ValueError('Group schema, split or feature order differs')
    groups = doc['groups']
    if not isinstance(groups, list) or not 1 <= len(groups) <= MAX_GROUPS:
        raise ValueError('Group count differs')
    seen_panels, seen_candidates = set(), set()
    for group in groups:
        if not isinstance(group, dict) or set(group) != {'source_sha256','panel_sha256','rows'}: raise ValueError('Invalid group')
        panel = (_hash(group['source_sha256']), _hash(group['panel_sha256']))
        if panel not in authorized_panels or panel in seen_panels: raise ValueError('Unbound or duplicate panel')
        seen_panels.add(panel)
        rows = group['rows']
        if not isinstance(rows, list) or not 2 <= len(rows) <= MAX_CANDIDATES: raise ValueError('Invalid candidate count')
        for index, row in enumerate(rows):
            if not isinstance(row, dict) or set(row) != {'rank','candidate_identity_sha256','features','target_error'} or type(row['rank']) is not int or row['rank'] != index + 1:
                raise ValueError('Candidate row differs')
            ident = _hash(row['candidate_identity_sha256'])
            if ident in seen_candidates: raise ValueError('Duplicate candidate')
            seen_candidates.add(ident)
            if not isinstance(row['features'], list) or len(row['features']) != len(FEATURE_NAMES): raise ValueError('Feature width differs')
            for value in row['features']: _number(value)
            if _number(row['target_error']) < 0: raise ValueError('Target error must be nonnegative')
    return groups

def schedule(recipe, group_count):
    if type(recipe) is not Recipe: raise ValueError('Exact recipe required')
    recipe.validate()
    if type(group_count) is not int or not 1 <= group_count <= MAX_GROUPS: raise ValueError('Invalid group count')
    result = []
    for epoch in range(recipe.epochs):
        indices = list(range(group_count)); random.Random(recipe.seed + epoch).shuffle(indices)
        result.extend(indices)
    return tuple(result)
