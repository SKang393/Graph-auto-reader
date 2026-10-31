# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Make two owned train-only rasters and keep truth in a separate output tree."""
from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path
from unittest.mock import patch

from ml.synthetic import renderer, templates
from ml.synthetic.contextual_negatives import render_contextual_scene
from ml.synthetic.fonts import ResolvedFont
from ml.synthetic.marker_context_catalog_v1 import catalog
from ml.synthetic.marker_context_overlay_v1.overlay import overlay
from ml.synthetic.public_fonts_v1.fonts import verify

ROOT = Path(__file__).resolve().parents[3]
RECIPE_INDICES = (8, 4)
SCHEMA = 'graphreader.marker-context-train-raster-input.v1'
SOURCE_PATHS = (
    'ml/synthetic/__init__.py', 'ml/synthetic/templates.py', 'ml/synthetic/renderer.py',
    'ml/synthetic/contextual_negatives.py', 'ml/synthetic/fonts.py', 'ml/synthetic/schema.py',
    'ml/synthetic/scene.schema.json', 'ml/synthetic/public_fonts_v1/__init__.py',
    'ml/synthetic/public_fonts_v1/fonts.py', 'ml/synthetic/public_fonts_v1/manifest.json',
    'ml/synthetic/public_fonts_v1/assets/LICENSE', 'ml/synthetic/public_fonts_v1/assets/NotoSans-Regular.ttf',
    'ml/synthetic/public_fonts_v1/assets/NotoSerif-Regular.ttf', 'ml/synthetic/public_fonts_v1/assets/NotoSansMono-Regular.ttf',
    'ml/synthetic/marker_context_catalog_v1/__init__.py', 'ml/synthetic/marker_context_catalog_v1/catalog.py',
    'ml/synthetic/marker_context_catalog_v1/labels.py', 'ml/synthetic/marker_context_overlay_v1/__init__.py',
    'ml/synthetic/marker_context_overlay_v1/overlay.py', 'ml/synthetic/marker_context_train_capture_v1/__init__.py',
    'ml/synthetic/marker_context_train_capture_v1/builder.py')


def _hash(raw): return sha256(raw).hexdigest()
def _json(value): return json.dumps(value, sort_keys=True, separators=(',', ':'), allow_nan=False).encode()


def source_binding():
    """Describe exact local generator bytes; caller must freeze digest separately."""
    sources = {}
    for relative in SOURCE_PATHS:
        path = ROOT / relative
        if not path.is_file() or not path.resolve(strict=True).is_relative_to(ROOT):
            raise ValueError('Generator source missing or outside project')
        sources[relative] = _hash(path.read_bytes())
    return {'schema': 'graphreader.marker-context-train-builder-binding.v1',
            'recipe_indices': list(RECIPE_INDICES), 'split': 'train', 'sources': sources,
            'training_approved': False}


def build(expected_binding_sha256):
    """Return raster-only manifest/images and separately held train truth.

    This is not an admission or a model input producer. No dev recipe is read.
    """
    if not isinstance(expected_binding_sha256, str) or _hash(_json(source_binding())) != expected_binding_sha256:
        raise ValueError('Independently fixed generator binding differs')
    font = verify()['sans']; font_hash = _hash(font.read_bytes())
    class Resolver:
        def __init__(self, *args, **kwargs): pass
        def resolve(self, requested, size_px):
            return ResolvedFont(str(requested), font, int(size_px), 'project-local-verified', font_hash, 'Noto Sans', 'Regular')
    sources, raster_payloads, truth = [], {}, []
    # Production renderer modules are patched only inside this short isolated
    # builder process. Do not run build concurrently with other threads.
    with patch.object(templates, 'FontResolver', Resolver), patch.object(renderer, 'FontResolver', Resolver):
        for index in RECIPE_INDICES:
            recipe = catalog.recipes('train')[index]
            scene = catalog.prepare_scene(templates.build_scene(recipe['design'], recipe['seed'], recipe['renderer_family'], panel_count=1), recipe)
            image, annotation, marker_mask = render_contextual_scene(scene, split='train')
            panel_id = scene['panels'][0]['panel_id']
            # Fixed deterministic seed-independent search; only own train raster
            # and positive mask are consulted to avoid overpainting graph ink.
            x, y, w, h = scene['panels'][0]['plot_box']
            output = None
            for yy in range(int(y)+20, int(y+h)-20, 12):
                for xx in range(int(x)+20, int(x+w)-40, 12):
                    candidate, sidecar = overlay(scene, annotation, image, marker_mask, recipe=recipe,
                                                 panel_id=panel_id, center=(xx, yy), cue=(index == 8))
                    if sidecar['materialized']:
                        output = (candidate, sidecar); break
                if output is not None: break
            if output is None: raise ValueError('No safe in-plot overlay for fixed train recipe')
            image, sidecar = output
            data = BytesIO(); image.save(data, format='PNG', optimize=False)
            raster = data.getvalue(); sha = _hash(raster); name = f'{sha}.png'
            if name in raster_payloads: raise ValueError('Duplicate train raster')
            raster_payloads[name] = raster
            sources.append({'file': name, 'sha256': sha, 'width': image.width, 'height': image.height})
            truth.append({'image_sha256': sha, 'scene': scene, 'annotation': annotation, 'overlay': sidecar})
    if _hash(_json(source_binding())) != expected_binding_sha256:
        raise ValueError('Generator source changed during rendering')
    manifest = {'schema': SCHEMA, 'split': 'train', 'builder_binding_sha256': expected_binding_sha256,
                'sources': sources, 'training_approved': False}
    return manifest, raster_payloads, truth


def write_new(output, expected_binding_sha256):
    """Write only a fresh project-artifacts directory; truth is outside raster root."""
    root = (ROOT / 'artifacts').resolve(strict=True)
    out = Path(output)
    if not out.is_absolute(): raise ValueError('Absolute project artifact output required')
    parent = out.parent.resolve(strict=True)
    if not parent.is_relative_to(root) or out.exists(): raise ValueError('New project artifact directory required')
    manifest, images, truth = build(expected_binding_sha256)
    out.mkdir()
    raster_root = out / 'raster-only'; raster_root.mkdir()
    truth_root = out / 'post-capture-truth'; truth_root.mkdir()
    for name, data in images.items():
        with (raster_root / name).open('xb') as stream: stream.write(data)
    manifest_bytes = _json(manifest)
    with (raster_root / 'manifest.json').open('xb') as stream: stream.write(manifest_bytes)
    truth_bytes = _json({'schema': 'graphreader.marker-context-train-truth.v1', 'sources': truth,
                         'training_approved': False})
    with (truth_root / 'truth.json').open('xb') as stream: stream.write(truth_bytes)
    return {'raster_manifest_sha256': _hash(manifest_bytes), 'truth_sha256': _hash(truth_bytes),
            'source_count': len(images), 'training_approved': False}
