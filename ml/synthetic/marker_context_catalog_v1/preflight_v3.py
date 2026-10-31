# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Bound, in-memory eight-scene train diagnostic; never trains a model."""
from __future__ import annotations

import argparse
from collections import Counter
from hashlib import sha256
import json
import os
from pathlib import Path
import platform
import sys
from unittest.mock import patch
from uuid import UUID, uuid5

ROOT = Path(__file__).resolve().parents[3]
PACKAGE = Path(__file__).resolve().parent
PROTOCOL = PACKAGE / 'preflight_binding_v3.json'
SOURCES = (
    'ml/synthetic/__init__.py',
    'ml/synthetic/marker_context_catalog_v1/__init__.py',
    'ml/synthetic/marker_context_catalog_v1/catalog.py',
    'ml/synthetic/marker_context_catalog_v1/labels.py',
    'ml/synthetic/marker_context_catalog_v1/preflight_v3.py',
    'ml/synthetic/templates.py', 'ml/synthetic/renderer.py',
    'ml/synthetic/contextual_negatives.py', 'ml/synthetic/fonts.py',
    'ml/synthetic/schema.py', 'ml/synthetic/scene.schema.json',
    'ml/synthetic/public_fonts_v1/__init__.py',
    'ml/synthetic/public_fonts_v1/fonts.py',
    'ml/synthetic/public_fonts_v1/manifest.json',
    'ml/synthetic/public_fonts_v1/assets/LICENSE',
    'ml/synthetic/public_fonts_v1/assets/NotoSans-Regular.ttf',
    'ml/synthetic/public_fonts_v1/assets/NotoSerif-Regular.ttf',
    'ml/synthetic/public_fonts_v1/assets/NotoSansMono-Regular.ttf',
)
INDICES = tuple(range(8))
DESIGNS = ('ab', 'abab', 'multiple_baseline', 'alternating_treatments')
MODES = ('connected', 'missing_edges')
REQUIRED = ('connected_positive', 'isolated_positive', 'contextual_negative')


def digest(data):
    return sha256(data).hexdigest()


def canonical(obj):
    return json.dumps(obj, sort_keys=True, separators=(',', ':'), allow_nan=False).encode()


def inventory():
    result = {}
    for relative in SOURCES:
        path = ROOT / relative
        if not path.is_file() or not path.resolve(strict=True).is_relative_to(ROOT):
            raise ValueError('Source missing or outside project')
        result[relative] = digest(path.read_bytes())
    return result


def snapshot():
    if (ROOT / 'ml/__init__.py').exists():
        raise ValueError('Unexpected ml package initializer')
    return {'schema': 'marker-context-preflight-binding-v3', 'sources': inventory(),
            'train_indices': list(INDICES), 'required_bins': list(REQUIRED),
            'designs': list(DESIGNS), 'connection_modes': list(MODES),
            'panel_count': 1, 'renderer_profile': 'historical',
            'font_family': 'Noto Sans', 'split': 'train', 'training_approved': False}


def verify_binding(expected):
    data = PROTOCOL.read_bytes()
    if digest(data) != expected:
        raise ValueError('Binding digest mismatch')
    binding = json.loads(data)
    if binding != snapshot():
        raise ValueError('Source, recipe or protocol binding differs')
    return binding


def require_coverage(counts):
    missing = [name for name in REQUIRED if counts.get(name, 0) <= 0]
    if missing:
        raise ValueError('Required bins have zero coverage: ' + ', '.join(missing))


def design_mode_coverage(rows):
    expected = {(design, mode) for design in DESIGNS for mode in MODES}
    actual = [(row['design'], row['connection_mode']) for row in rows]
    if len(actual) != len(expected) or set(actual) != expected:
        raise ValueError('Design/mode grid is incomplete or duplicated')
    result = []
    for row in rows:
        positive = 'connected_positive' if row['connection_mode'] == 'connected' else 'isolated_positive'
        counts = row['counts']
        if counts.get(positive, 0) <= 0:
            raise ValueError('Design/mode cell has no expected positives')
        if row['visible_legend_glyphs'] != counts.get('contextual_negative', 0):
            raise ValueError('Contextual glyph coverage differs')
        result.append({'design': row['design'], 'connection_mode': row['connection_mode'],
                       'counts': counts, 'visible_legend_glyphs': row['visible_legend_glyphs']})
    return result


def check_identities(train, dev, selected, scene_id_for):
    for rows in (train, dev, selected):
        if len({r['seed'] for r in rows}) != len(rows):
            raise ValueError('Duplicate recipe seed')
    if any(r['split'] != 'train' or r not in train for r in selected):
        raise ValueError('Selected recipe is not train')
    if {r['seed'] for r in train} & {r['seed'] for r in dev}:
        raise ValueError('Cross-split seed collision')
    train_ids = [scene_id_for(r) for r in train]
    dev_ids = [scene_id_for(r) for r in dev]
    if len(set(train_ids)) != len(train_ids) or len(set(dev_ids)) != len(dev_ids) or set(train_ids) & set(dev_ids):
        raise ValueError('Duplicate or cross-split scene identity')
    return train_ids, dev_ids


def glyph_visible(scene, original, center, box, renderer, render):
    """Prove actual rendered glyph contribution by a single-glyph ablation."""
    import numpy as np
    observed = []
    original_draw = renderer._draw_marker
    def draw(draw_context, mask_context, xy, *args):
        if tuple(xy) == tuple(center):
            observed.append(tuple(xy))
            return None
        return original_draw(draw_context, mask_context, xy, *args)
    with patch.object(renderer, '_draw_marker', draw):
        without, _, _ = render(scene, split='train')
    if len(observed) != 1:
        raise ValueError('Glyph ablation was not unique')
    changed = np.any(np.asarray(original.convert('RGB')) != np.asarray(without.convert('RGB')), axis=2)
    x, y, w, h = box
    import math
    # Pillow includes the right and bottom coordinate for outlined markers.
    x0, y0 = math.floor(x), math.floor(y)
    x1, y1 = min(changed.shape[1], math.ceil(x+w)+1), min(changed.shape[0], math.ceil(y+h)+1)
    inner = changed[y0:y1, x0:x1]
    if not inner.any() or int(inner.sum()) != int(changed.sum()):
        raise ValueError('Glyph has no uniquely attributable visible pixels in its box')
    return int(inner.sum())


def run(expected):
    binding = verify_binding(expected)
    # Application imports occur only after the supplied source snapshot is verified.
    sys.path.insert(0, str(ROOT))
    import numpy as np
    import PIL
    from ml.synthetic import renderer, templates
    from ml.synthetic.contextual_negatives import render_contextual_scene
    from ml.synthetic.fonts import ResolvedFont
    from ml.synthetic.marker_context_catalog_v1 import catalog, labels
    from ml.synthetic.public_fonts_v1.fonts import verify

    if {Path(p).resolve() for p in sys.modules['ml'].__path__} != {(ROOT / 'ml').resolve()}:
        raise ValueError('Unexpected ml namespace search path')

    font = verify()['sans']
    font_hash = digest(font.read_bytes())
    class ProjectFontResolver:
        def __init__(self, *args, **kwargs): pass
        def resolve(self, requested, size_px):
            return ResolvedFont(str(requested), font, int(size_px), 'project-local-verified', font_hash, 'Noto Sans', 'Regular')

    train, dev = catalog.recipes('train'), catalog.recipes('dev')
    selected = [train[i] for i in INDICES]
    def scene_id_for(recipe):
        base = templates._stable_uuid(recipe['seed'], recipe['design'], recipe['renderer_family'], 'scene')
        return str(uuid5(UUID(base), f"{catalog.REVISION}:{recipe['split']}:{recipe['seed']}:{recipe['connection_mode']}"))
    train_ids, dev_ids = check_identities(train, dev, selected, scene_id_for)
    counts, rows, seen_images, seen_scenes = Counter(), [], set(), set()
    with patch.object(templates, 'FontResolver', ProjectFontResolver), patch.object(renderer, 'FontResolver', ProjectFontResolver):
        for recipe in selected:
            scene = templates.build_scene(recipe['design'], recipe['seed'], recipe['renderer_family'], panel_count=1)
            prepared = catalog.prepare_scene(scene, recipe)
            if prepared['scene_id'] != scene_id_for(recipe) or prepared['scene_id'] in seen_scenes:
                raise ValueError('Rendered train scene identity differs')
            image, annotation, marker_mask = render_contextual_scene(prepared, split='train')
            scene_sha = digest(canonical(prepared))
            if annotation['contextual_negative_profile']['source_scene_canonical_sha256'] != scene_sha:
                raise ValueError('Annotation scene binding differs')
            result = labels.classify(annotation, expected_split='train')
            visible = 0
            negatives = {n['hard_negative_id']: n for n in annotation['hard_negatives'] if n['kind'] == 'legend_symbol'}
            for record in result['records']:
                if record['bin'] == 'contextual_negative':
                    n = negatives[record['source_id']]
                    glyph_visible(prepared, image, n['geometry']['center'], n['geometry']['box'], renderer, render_contextual_scene)
                    visible += 1
            image_sha = digest(image.convert('RGB').tobytes())
            if image_sha in seen_images:
                raise ValueError('Duplicate rendered image')
            seen_scenes.add(prepared['scene_id']); seen_images.add(image_sha)
            counts.update(result['counts'])
            rows.append({'recipe_index': recipe['index'], 'design': recipe['design'],
                         'connection_mode': recipe['connection_mode'], 'scene_id': prepared['scene_id'],
                         'scene_sha256': scene_sha, 'image_rgb_sha256': image_sha,
                         'annotation_sha256': digest(canonical(annotation)),
                         'marker_mask_sha256': digest(marker_mask.tobytes()),
                         'counts': result['counts'], 'visible_legend_glyphs': visible})
    coverage_by_design_mode = design_mode_coverage(rows)
    try:
        require_coverage(counts)
        coverage_error = None
    except ValueError as error:
        coverage_error = str(error)
    verify_binding(expected)
    return {'status': 'preflight_failed_empty_bins' if coverage_error else 'preflight_passed_only',
            'coverage_error': coverage_error, 'binding_sha256': expected, 'scene_count': len(rows),
            'counts': dict(counts), 'coverage_by_design_mode': coverage_by_design_mode,
            'scenes': rows, 'train_catalog_scene_count': len(train_ids),
            'dev_recipe_identity_count_without_rendering': len(dev_ids), 'font_sha256': font_hash,
            'python_version': platform.python_version(), 'pillow_version': PIL.__version__,
            'numpy_version': np.__version__, 'training_approved': False, 'optimizer_steps': 0,
            'private_read': False, 'sealed_read': False, 'dev_rendered': False,
            'limitations': ['Binding is an externally supplied digest, not a signature or OS attestation.',
              'Dependencies and source files are checked before and after, not locked against concurrent writes.',
              'Eight in-memory train scenes share generator families with dev; external independence is unproved.',
              'Glyph ablation proves visible contribution under this renderer; production crop/mask parity is unproved.']}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--freeze', action='store_true')
    parser.add_argument('--binding-sha256')
    args = parser.parse_args()
    if args.freeze:
        if args.binding_sha256: parser.error('Choose freeze or run')
        payload = canonical(snapshot()) + b'\n'
        with PROTOCOL.open('xb') as stream: stream.write(payload)
        print(digest(payload))
    else:
        if not args.binding_sha256: parser.error('A separately supplied binding digest is required')
        result = run(args.binding_sha256)
        print(json.dumps(result, sort_keys=True, allow_nan=False))
        if result['status'] != 'preflight_passed_only': raise SystemExit(1)

if __name__ == '__main__': main()
