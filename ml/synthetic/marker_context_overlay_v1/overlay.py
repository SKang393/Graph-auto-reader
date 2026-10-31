# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Small, train-only in-plot overlay diagnostic, never a training admission."""
from hashlib import sha256
import importlib.util
import json
import math
from pathlib import Path
from types import FunctionType
from uuid import UUID, uuid5

from PIL import Image, ImageChops, ImageDraw, ImageFont
from ml.synthetic import renderer, templates
from ml.synthetic import contextual_negatives
from ml.synthetic.fonts import ResolvedFont
from ml.synthetic.marker_context_catalog_v1 import catalog, labels
from ml.synthetic.public_fonts_v1.fonts import verify
from ml.synthetic.schema import validate_scene


def _hash(data): return sha256(data).hexdigest()
def _json(value): return json.dumps(value, sort_keys=True, separators=(',', ':'), allow_nan=False).encode()


def _verify_rerender(scene, annotation, image, mask, font_path, font_hash):
    """Rerender with an isolated renderer namespace, avoiding shared patches."""
    if annotation.get('contextual_negative_profile', {}).get('source_profile') != 'historical':
        raise ValueError('Only the historical contextual source profile is supported')
    spec = importlib.util.spec_from_file_location('ml.synthetic._marker_overlay_renderer', renderer.__file__)
    if spec is None or spec.loader is None: raise ValueError('Cannot load renderer source')
    isolated = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(isolated)
    class ProjectFontResolver:
        def __init__(self, *args, **kwargs): pass
        def resolve(self, requested, size_px):
            return ResolvedFont(str(requested), font_path, int(size_px), 'project-local-verified', font_hash, 'Noto Sans', 'Regular')
    isolated.FontResolver = ProjectFontResolver
    source = contextual_negatives.render_contextual_scene
    render = FunctionType(source.__code__, {**source.__globals__, 'render_scene': isolated.render_scene}, source.__name__, source.__defaults__, source.__closure__)
    render.__kwdefaults__ = source.__kwdefaults__
    expected_image, expected_annotation, expected_mask = render(scene, split='train', source_profile='historical')
    if expected_image.mode != image.mode or expected_image.size != image.size or expected_image.tobytes() != image.tobytes():
        raise ValueError('Input image differs from verified rerender')
    if expected_mask.mode != mask.mode or expected_mask.size != mask.size or expected_mask.tobytes() != mask.tobytes():
        raise ValueError('Input marker mask differs from verified rerender')
    if _json(expected_annotation) != _json(annotation):
        raise ValueError('Input annotation differs from verified rerender')
    return isolated


def overlay(scene, annotation, base_image, marker_mask, *, recipe, panel_id, center, cue):
    """Return a new image and descriptive sidecar; every row remains review-only.

    Inputs must match a fresh process-local rerender, which is not a signed
    producer receipt. Production patch and mask parity remain unclaimed.
    """
    if type(cue) is not bool: raise ValueError('cue must be boolean')
    if not isinstance(recipe, dict) or recipe not in catalog.recipes('train'):
        raise ValueError('An exact train recipe is required')
    validate_scene(scene)
    if (scene['seed'], scene['design'], scene['families']['renderer']['key']) != (recipe['seed'], recipe['design'], recipe['renderer_family']):
        raise ValueError('Scene does not match recipe')
    expected_base = templates._stable_uuid(recipe['seed'], recipe['design'], recipe['renderer_family'], 'scene')
    expected_id = str(uuid5(UUID(expected_base), f"{catalog.REVISION}:train:{recipe['seed']}:{recipe['connection_mode']}"))
    if scene['scene_id'] != expected_id: raise ValueError('Scene identity differs')
    expected_style = 'solid' if recipe['connection_mode'] == 'connected' else 'missing'
    expected_visible = recipe['connection_mode'] == 'connected'
    if scene['presentation']['connecting_line_style'] != expected_style:
        raise ValueError('Presentation connection mode differs from recipe')
    connection_count = 0
    for panel in scene['panels']:
        if any(series['line_style'] != expected_style for series in panel['series']):
            raise ValueError('Series connection mode differs from recipe')
        for connection in panel['connections']:
            if connection['style'] != expected_style or connection['visible'] is not expected_visible:
                raise ValueError('Connection mode differs from recipe')
            connection_count += 1
    if not connection_count: raise ValueError('No connections to verify')
    provenance = scene['provenance']
    if provenance['source'] != 'procedural' or provenance['license'] != 'Apache-2.0' or provenance['private_data'] or provenance['external_assets']:
        raise ValueError('Scene provenance differs')
    if any(stage['kind'] != 'none' for stage in scene['degradations']) or annotation.get('transforms') or any(stage['kind'] != 'none' for stage in annotation.get('degradations', [])):
        raise ValueError('Only identity geometry and no-op degradations are supported')
    source_hash = _hash(_json(scene))
    profile = annotation.get('contextual_negative_profile', {})
    if profile.get('source_scene_canonical_sha256') != source_hash:
        raise ValueError('Annotation source hash differs')
    classified = labels.classify(annotation, expected_split='train')
    if classified['scene_id'] != scene['scene_id']: raise ValueError('Annotation scene identity differs')
    if base_image.mode != 'RGB' or marker_mask.mode != 'L' or base_image.size != marker_mask.size or base_image.size != (scene['canvas']['width'], scene['canvas']['height']):
        raise ValueError('Raster dimensions or modes differ')
    source_panels = [p for p in scene['panels'] if p['panel_id'] == panel_id]
    if len(source_panels) != 1 or len([p for p in annotation['panels'] if p['panel_id'] == panel_id]) != 1:
        raise ValueError('Panel identity differs')
    if not isinstance(center, (list, tuple)) or len(center) != 2 or any(type(x) is not int for x in center):
        raise ValueError('Center must contain integer pixel coordinates')
    cx, cy = center
    x, y, width, height = source_panels[0]['plot_box']
    if not (x <= cx-6 and cx+6 < x+width and y <= cy-6 and cy+6 < y+height):
        raise ValueError('Glyph must be fully inside the plot')

    font_path = verify()['sans']
    font_hash = _hash(font_path.read_bytes())
    font_info = annotation.get('font', {})
    if font_info.get('sha256') != font_hash or Path(font_info.get('resolved_path', '')).resolve() != font_path.resolve():
        raise ValueError('Base font provenance differs from verified project font')
    isolated_renderer = _verify_rerender(scene, annotation, base_image, marker_mask, font_path, font_hash)
    common = {'schema': 'marker-context-inplot-overlay-v1', 'split': 'train', 'seed': recipe['seed'],
              'scene_id': scene['scene_id'], 'scene_sha256': source_hash, 'panel_id': panel_id,
              'base_rgb_sha256': _hash(base_image.tobytes()), 'marker_mask_sha256': _hash(marker_mask.tobytes()),
              'font_sha256': font_hash, 'center': [cx, cy], 'optimization_allowed': False,
              'production_crop_mask_parity_verified': False}

    glyph_image = Image.new('RGB', base_image.size, 'white')
    glyph_mask = Image.new('L', base_image.size, 0)
    isolated_renderer._draw_marker(ImageDraw.Draw(glyph_image), ImageDraw.Draw(glyph_mask), (cx, cy), 5, 'circle', 'open', (0, 0, 0), 1)
    ink = ImageChops.invert(glyph_image.convert('L'))
    cue_mask = Image.new('L', base_image.size, 0)
    if cue:
        # A simple explicit annotation cue; it is an artificial context, not a
        # claim about the unexplained real-world circles.
        ImageDraw.Draw(cue_mask).text((cx+8, cy-7), 'note', font=ImageFont.truetype(str(font_path), 10), fill=255)
        bounds = cue_mask.getbbox()
        if bounds is None: raise ValueError('Cue produced no visible pixels')
        left, top, right, bottom = bounds
        if not (x <= left and right <= x+width and y <= top and bottom <= y+height and
                cx-32 <= left and right <= cx+33 and cy-32 <= top and bottom <= cy+33):
            raise ValueError('Cue is outside plot or nominal context window')
        if ImageChops.multiply(cue_mask, glyph_mask).getbbox(): raise ValueError('Cue overlaps glyph')
    combined = ImageChops.lighter(ink, cue_mask)
    if not ink.getbbox(): raise ValueError('Glyph produced no ink')
    if ImageChops.multiply(ImageChops.lighter(glyph_mask, cue_mask), marker_mask).getbbox():
        return base_image.copy(), {**common, 'status': 'review_only', 'reason': 'positive_marker_overlap', 'materialized': False}
    # Overpainting existing pixels can obscure scientific marks or hide the cue.
    dark = ImageChops.invert(base_image.convert('L')).point(lambda value: 255 if value else 0)
    if ImageChops.multiply(ImageChops.lighter(glyph_mask, cue_mask), dark).getbbox():
        return base_image.copy(), {**common, 'status': 'review_only', 'reason': 'existing_ink_overlap', 'materialized': False}
    result = base_image.copy()
    result.paste((0, 0, 0), mask=combined)
    changed = ImageChops.difference(result, base_image).convert('L')
    if not ImageChops.multiply(changed, ink).getbbox() or (cue and not ImageChops.multiply(changed, cue_mask).getbbox()):
        raise ValueError('Overlay glyph or cue is not visible')
    controls = [r['source_id'] for r in classified['records'] if r['bin'] == 'isolated_positive' and r['panel_id'] == panel_id]
    sidecar = {**common, 'output_rgb_sha256': _hash(result.tobytes()),
               'origin': 'synthetic_in_plot_annotation_glyph', 'shape': 'circle', 'fill': 'open',
               'cue_visible': cue, 'cue_text': 'note' if cue else None, 'cue_box': list(cue_mask.getbbox()) if cue else None,
               'status': 'review_only', 'reason': 'runtime_input_parity_and_identity_unverified',
               'materialized': True, 'semantic_non_data': True, 'isolated_positive_control_ids': controls}
    return result, sidecar
