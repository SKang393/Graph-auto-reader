# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Bound metadata and conservative post-capture association; no tensor loading.

Digests must come from an independently trusted caller. This module verifies
consistency only and does not establish the origin of the supplied metadata.
"""
from collections import Counter
from dataclasses import dataclass
from hashlib import sha256
import json
import math
import re

_SHA = re.compile(r'^[0-9a-f]{64}$')


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':'), allow_nan=False).encode()


def digest(value): return sha256(value).hexdigest()


def _read(raw, expected):
    if type(raw) is not bytes or not isinstance(expected, str) or not _SHA.fullmatch(expected) or digest(raw) != expected:
        raise ValueError('Trusted payload digest differs')
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result: raise ValueError('Duplicate JSON key')
            result[key] = value
        return result
    value = json.loads(raw, object_pairs_hook=pairs, parse_constant=lambda _: (_ for _ in ()).throw(ValueError('Nonfinite JSON')))
    if canonical(value) != raw: raise ValueError('Canonical JSON bytes required')
    return value


def _keys(value, names):
    if not isinstance(value, dict) or set(value) != set(names): raise ValueError('Unexpected or missing fields')


def _id(value):
    if not isinstance(value, str) or not value or len(value) > 200: raise ValueError('Invalid identity')
    return value


def _sha(value):
    if not isinstance(value, str) or not _SHA.fullmatch(value): raise ValueError('Invalid SHA-256')
    return value


def _xy(row, width, height):
    x, y = row['x'], row['y']
    if any(type(v) not in (int, float) or not math.isfinite(v) for v in (x, y)) or not (0 <= x < width and 0 <= y < height):
        raise ValueError('Invalid original-pixel coordinate')


@dataclass(frozen=True)
class FrozenCapture:
    manifest_bytes: bytes
    capture_bytes: bytes
    manifest_sha256: str
    capture_sha256: str


def freeze_capture(manifest_bytes, manifest_sha256, capture_bytes, capture_sha256):
    """Verify a truth-free capture against a source-identity-only split manifest."""
    manifest = _read(manifest_bytes, manifest_sha256)
    _keys(manifest, ('schema', 'match_tolerance_px', 'sources'))
    if manifest['schema'] != 'dual-view-source-identities-v1' or not isinstance(manifest['sources'], list) or not manifest['sources']:
        raise ValueError('Source manifest differs')
    tolerance = manifest['match_tolerance_px']
    if type(tolerance) not in (int, float) or not math.isfinite(tolerance) or not 0 < tolerance <= 5:
        raise ValueError('A finite prebound tolerance at most 5px is required')
    sources, image_hashes = {}, set()
    for row in manifest['sources']:
        _keys(row, ('source_id', 'image_sha256', 'split', 'width', 'height'))
        name = _id(row['source_id']); image = _sha(row['image_sha256'])
        if row['split'] not in ('train', 'dev') or name in sources or image in image_hashes:
            raise ValueError('Source identity or split collision')
        if any(type(row[n]) is not int or not 1 <= row[n] <= 100000 for n in ('width', 'height')):
            raise ValueError('Invalid source dimensions')
        sources[name] = row; image_hashes.add(image)
    train = {name for name, row in sources.items() if row['split'] == 'train'}
    dev = {name for name, row in sources.items() if row['split'] == 'dev'}
    if not train or not dev: raise ValueError('Both source split identities are required')
    capture = _read(capture_bytes, capture_sha256)
    _keys(capture, ('schema', 'manifest_sha256', 'runtime_sha256', 'coordinate_space', 'sources'))
    if (capture['schema'] != 'dual-view-observed-proposals-v1' or capture['manifest_sha256'] != manifest_sha256 or
            capture['coordinate_space'] != 'original_pixels' or not isinstance(capture['sources'], list)):
        raise ValueError('Capture contract differs')
    _sha(capture['runtime_sha256'])
    seen = set()
    for row in capture['sources']:
        _keys(row, ('source_id', 'image_sha256', 'status', 'proposals'))
        name = _id(row['source_id'])
        if name not in train or name in seen or row['image_sha256'] != sources[name]['image_sha256']:
            raise ValueError('Capture has unexpected, duplicate, or non-train source')
        seen.add(name)
        if row['status'] not in ('success', 'failed') or not isinstance(row['proposals'], list) or (row['status'] == 'failed' and row['proposals']):
            raise ValueError('Capture status differs')
        ids = set()
        for proposal in row['proposals']:
            _keys(proposal, ('proposal_id', 'x', 'y', 'radius', 'local_tensor_sha256', 'context_tensor_sha256'))
            pid = _id(proposal['proposal_id'])
            if pid in ids: raise ValueError('Duplicate proposal ID')
            ids.add(pid); _xy(proposal, sources[name]['width'], sources[name]['height'])
            if type(proposal['radius']) not in (int, float) or not math.isfinite(proposal['radius']) or not 0 < proposal['radius'] <= 100000:
                raise ValueError('Invalid proposal radius')
            _sha(proposal['local_tensor_sha256']); _sha(proposal['context_tensor_sha256'])
    if seen != train: raise ValueError('Capture must include every train source, including failures')
    return FrozenCapture(manifest_bytes, capture_bytes, manifest_sha256, capture_sha256)


def associate(frozen, truth_bytes, truth_sha256):
    """Attach truth only after frozen capture; ambiguous and negatives stay review-only.

    A source represents one already-bound original-pixel frame. Arbitrary panel
    mapping or resampling must be established by a future producer first.
    """
    if not isinstance(frozen, FrozenCapture): raise ValueError('A frozen capture is required')
    # Recheck bytes rather than trusting mutable caller-owned metadata.
    frozen = freeze_capture(frozen.manifest_bytes, frozen.manifest_sha256, frozen.capture_bytes, frozen.capture_sha256)
    manifest = _read(frozen.manifest_bytes, frozen.manifest_sha256)
    tolerance_px = manifest['match_tolerance_px']
    capture = _read(frozen.capture_bytes, frozen.capture_sha256)
    truth = _read(truth_bytes, truth_sha256)
    _keys(truth, ('schema', 'capture_sha256', 'sources'))
    if truth['schema'] != 'dual-view-postcapture-truth-v1' or truth['capture_sha256'] != frozen.capture_sha256 or not isinstance(truth['sources'], list):
        raise ValueError('Truth binding differs')
    manifest_rows = {row['source_id']: row for row in manifest['sources']}
    captures = {row['source_id']: row for row in capture['sources']}
    truths = {}
    for row in truth['sources']:
        _keys(row, ('source_id', 'image_sha256', 'markers', 'overlays'))
        name = _id(row['source_id'])
        if name not in captures or name in truths or row['image_sha256'] != manifest_rows[name]['image_sha256']:
            raise ValueError('Truth source identity differs')
        if not isinstance(row['markers'], list) or not isinstance(row['overlays'], list): raise ValueError('Truth rows must be lists')
        ids = set()
        for marker in row['markers']:
            _keys(marker, ('marker_id', 'x', 'y', 'connection'))
            mid = _id(marker['marker_id'])
            if mid in ids or marker['connection'] not in ('connected', 'isolated'): raise ValueError('Invalid marker identity or connection')
            ids.add(mid); _xy(marker, manifest_rows[name]['width'], manifest_rows[name]['height'])
        ids = set()
        for item in row['overlays']:
            _keys(item, ('overlay_id', 'x', 'y', 'status'))
            oid = _id(item['overlay_id'])
            if oid in ids or item['status'] != 'review_only': raise ValueError('Overlay must remain review-only')
            ids.add(oid); _xy(item, manifest_rows[name]['width'], manifest_rows[name]['height'])
        truths[name] = row
    if set(truths) != set(captures): raise ValueError('Truth must account for every train source including failures')
    result_sources = []
    for name in sorted(captures):
        capture_row, truth_row = captures[name], truths[name]
        proposals, markers, overlays = capture_row['proposals'], truth_row['markers'], truth_row['overlays']
        marker_links = [[j for j, marker in enumerate(markers) if math.hypot(p['x']-marker['x'], p['y']-marker['y']) <= tolerance_px] for p in proposals]
        reverse = [[i for i, links in enumerate(marker_links) if j in links] for j in range(len(markers))]
        overlay_links = [[j for j, item in enumerate(overlays) if math.hypot(p['x']-item['x'], p['y']-item['y']) <= tolerance_px] for p in proposals]
        coordinate_counts = Counter((p['x'], p['y']) for p in proposals)
        assignments = []
        for i, proposal in enumerate(proposals):
            links = marker_links[i]
            duplicate = coordinate_counts[(proposal['x'], proposal['y'])] > 1
            if overlay_links[i]: status = 'review_overlay'
            elif duplicate: status = 'review_duplicate'
            elif not links: status = 'review_unmatched'
            elif len(links) != 1 or len(reverse[links[0]]) != 1: status = 'review_ambiguous'
            elif markers[links[0]]['connection'] == 'isolated': status = 'review_isolated_positive'
            else: status = 'connected_positive_pending'
            assignments.append({'proposal_id': proposal['proposal_id'], 'status': status,
                                'marker_id': markers[links[0]]['marker_id'] if len(links) == 1 and len(reverse[links[0]]) == 1 else None,
                                'local_tensor_sha256': proposal['local_tensor_sha256'],
                                'context_tensor_sha256': proposal['context_tensor_sha256'],
                                'source_balanced_weight': 0.0})
        truth_status = []
        for j, marker in enumerate(markers):
            links = reverse[j]
            if capture_row['status'] == 'failed': status = 'source_failed'
            elif not links: status = 'unmatched'
            elif len(links) == 1 and assignments[links[0]]['status'] == 'connected_positive_pending': status = 'matched_connected'
            elif len(links) == 1 and assignments[links[0]]['status'] == 'review_isolated_positive': status = 'matched_isolated_review'
            else: status = 'review_ambiguous'
            truth_status.append({'marker_id': marker['marker_id'], 'status': status})
        matched_overlays = {j for links in overlay_links for j in links}
        result_sources.append({'source_id': name, 'capture_status': capture_row['status'], 'proposals': assignments, 'markers': truth_status,
                               'overlays': [{'overlay_id': item['overlay_id'], 'status': 'matched_review' if j in matched_overlays else 'unmatched_review'}
                                            for j, item in enumerate(overlays)]})
    # A paired tensor identity seen in any review-only context cannot produce
    # a pending supervised row, even when its own geometric match is unique.
    tensor_rows = {}
    for source in result_sources:
        for row in source['proposals']:
            key = (row['local_tensor_sha256'], row['context_tensor_sha256'])
            tensor_rows.setdefault(key, []).append((source, row))
    for group in tensor_rows.values():
        if any(row['status'] != 'connected_positive_pending' for _, row in group):
            for source, row in group:
                if row['status'] == 'connected_positive_pending':
                    row['status'] = 'review_conflicting_input'
                    for marker in source['markers']:
                        if marker['marker_id'] == row['marker_id']:
                            marker['status'] = 'review_ambiguous'
    eligible = [source for source in result_sources if any(row['status'] == 'connected_positive_pending' for row in source['proposals'])]
    for source in eligible:
        selected = [row for row in source['proposals'] if row['status'] == 'connected_positive_pending']
        for row in selected: row['source_balanced_weight'] = 1.0 / (len(eligible) * len(selected))
    counts = Counter(row['status'] for source in result_sources for row in source['proposals'])
    truth_counts = Counter(row['status'] for source in result_sources for row in source['markers'])
    return {'schema': 'dual-view-association-diagnostic-v1', 'manifest_sha256': frozen.manifest_sha256,
            'capture_sha256': frozen.capture_sha256, 'truth_sha256': truth_sha256, 'tolerance_px': tolerance_px,
            'source_count': len(result_sources), 'source_failures': sum(s['capture_status'] == 'failed' for s in result_sources),
            'proposal_counts': dict(counts), 'truth_counts': dict(truth_counts), 'sources': result_sources,
            'negative_training_rows': 0, 'training_approved': False, 'optimizer_steps': 0}
