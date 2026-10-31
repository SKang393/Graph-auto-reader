# SPDX-License-Identifier: Apache-2.0
"""Bound in-memory legend-glyph render/ablation producer for synthetic scenes."""
from contextlib import ExitStack
from copy import deepcopy
from hashlib import sha256
import json
from pathlib import Path
from io import BytesIO
from unittest.mock import patch

from .association import build_visibility_evidence

ROOT = Path(__file__).resolve().parents[2]
UPSTREAM_SOURCE_SHA256 = {
    "ml/synthetic/__init__.py": "6875b53c0bbbd101cb06ff7987f06ea66520493f5361456ebc1d724870d13add",
    "ml/synthetic/renderer.py": "c49c070afc505f92bfff7ff55b22e669c3ec2b76b56ff807b72a026ca0535283",
    "ml/synthetic/contextual_negatives.py": "8c5d18199b5a4250f22f24557452b5aab9063c3ed62a69ed0c6a46663371d7c1",
    "ml/synthetic/schema.py": "237d9296f2f72a11bb7020c2abac022026f72a56e4e0eac2d322f23a3e6c8acb",
    "ml/synthetic/fonts.py": "c4ec7a2333e0410d4c49cc41c154d3b80a2898c5682a135c25e1162a806d58fc",
    "ml/synthetic/templates.py": "f59733c5f0589bd1b9143d3c9ae83015b4c53ab593de229d56e46d9f11ec2f1c",
    "ml/synthetic/marker_context_catalog_v1/catalog.py": "7f387c665e081e9a838d56983587960ece4f1b337a8c1f57313353077829e288",
    "ml/synthetic/public_fonts_v1/__init__.py": "ab8675646268c77966817419e81dc6b7eeaff56e233fb97e5a6dcd69940a0afa",
    "ml/synthetic/public_fonts_v1/fonts.py": "b2094803615a56ed472a38f0f741ddbbe938758f9629f39b96c2d834a853f76d",
    "ml/synthetic/public_fonts_v1/manifest.json": "0eb134a6e980e3042360b6477bf2ce178921d7dc23b3ded9ba12ee661b9470a2",
    "ml/synthetic/public_fonts_v1/assets/LICENSE": "0dab92d0544f7b233403f14b84a663bdbfa746982eda629e7f4f9ffe1b036feb",
    "ml/synthetic/public_fonts_v1/assets/NotoSans-Regular.ttf": "b85c38ecea8a7cfb39c24e395a4007474fa5a4fc864f6ee33309eb4948d232d5",
}
EXPECTED_PILLOW_VERSION = "12.3.0"
EXPECTED_NUMPY_VERSION = "2.3.5"


def _canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False).encode("utf-8")


def _digest(raw):
    return sha256(raw).hexdigest()


def _safe_bound_bytes(relative):
    path = ROOT
    for part in Path(relative).parts:
        path = path / part
        info = path.lstat()
        attributes = getattr(info, "st_file_attributes", 0)
        if path.is_symlink() or attributes & 0x400:
            raise ValueError("renderer source binding contains a link or reparse point")
    resolved = path.resolve(strict=True)
    if not resolved.is_relative_to(ROOT.resolve(strict=True)) or not resolved.is_file():
        raise ValueError("renderer source binding escapes the project")
    return resolved.read_bytes()


def verify_renderer_bundle():
    """Check fixed renderer/font bytes before importing their modules."""
    observed = {}
    for relative, expected in UPSTREAM_SOURCE_SHA256.items():
        actual = _digest(_safe_bound_bytes(relative))
        if actual != expected:
            raise ValueError(f"pinned renderer source changed: {relative}")
        observed[relative] = actual
    return {
        "schema": "graphreader.legend-ablation-renderer-bundle.v1",
        "sources": observed,
        "pillow_version": EXPECTED_PILLOW_VERSION,
        "numpy_version": EXPECTED_NUMPY_VERSION,
        "sha256": _digest(_canonical({
            "sources": observed,
            "pillow_version": EXPECTED_PILLOW_VERSION,
            "numpy_version": EXPECTED_NUMPY_VERSION,
        })),
    }


def _call_signature(center, radius, shape, fill_state, color, stroke_width):
    return (tuple(float(value) for value in center), float(radius), str(shape), str(fill_state),
            tuple(int(value) for value in color), int(stroke_width))


def _rgb_png(image):
    buffer = BytesIO()
    image.convert("RGB").save(buffer, format="PNG", optimize=False, compress_level=9)
    return buffer.getvalue()


def produce_legend_symbol_visibility(*, seed, expected_split, legend_id=None, entry_index=None):
    """Build one fixed train recipe twice and ablate exactly one legend draw.

    The scene is generated in memory from the fixed train catalog, never loaded
    from a path. The recipe, renderer/font source closure, and package font are checked before rendering.
    A matching renderer call is selected by the native legend annotation's
    center/radius/shape/fill, and must occur exactly once. The second render
    suppresses that single ordinal call only. Ambiguity fails closed.
    """
    if expected_split != "train":
        raise ValueError("legend visibility producer is train-only")
    bundle = verify_renderer_bundle()

    import numpy as np
    import PIL
    from PIL import ImageFont
    from ml.synthetic import renderer, templates
    from ml.synthetic.contextual_negatives import render_contextual_scene
    from ml.synthetic.fonts import ResolvedFont
    from ml.synthetic.marker_context_catalog_v1 import catalog
    from ml.synthetic.public_fonts_v1.fonts import verify as verify_fonts

    if PIL.__version__ != EXPECTED_PILLOW_VERSION or np.__version__ != EXPECTED_NUMPY_VERSION:
        raise ValueError("Pillow/NumPy runtime differs from the pinned producer protocol")
    if type(seed) is not int:
        raise ValueError("seed must be an integer from the fixed train catalog")
    recipes = {recipe["seed"]: recipe for recipe in catalog.recipes("train")}
    recipe = recipes.get(seed)
    if recipe is None:
        raise ValueError("scene seed is outside the fixed synthetic train recipe")

    fonts = verify_fonts()
    font_path = fonts["sans"]
    font_sha = _digest(font_path.read_bytes())
    font_family, font_style = ImageFont.truetype(str(font_path), 24).getname()

    class PinnedFontResolver:
        def __init__(self, *args, **kwargs):
            pass

        def resolve(self, requested=None, size_px=14):
            return ResolvedFont(str(requested or "Noto Sans"), font_path, size_px,
                                "package_asset", font_sha, font_family, font_style)

    base_calls = []
    original_draw = renderer._draw_marker

    def recording_draw(draw, mask_draw, center, radius, shape, fill_state, color, stroke_width):
        base_calls.append(_call_signature(center, radius, shape, fill_state, color, stroke_width))
        return original_draw(draw, mask_draw, center, radius, shape, fill_state, color, stroke_width)

    with ExitStack() as stack:
        stack.enter_context(patch.object(templates, "FontResolver", PinnedFontResolver))
        stack.enter_context(patch.object(renderer, "FontResolver", PinnedFontResolver))
        source_scene = templates.build_scene(
            recipe["design"], recipe["seed"], recipe["renderer_family"], panel_count=1)
        prepared = catalog.prepare_scene(source_scene, recipe)
        stack.enter_context(patch.object(renderer, "_draw_marker", recording_draw))
        baseline_image, baseline_annotation, _ = render_contextual_scene(
            deepcopy(prepared), split="train", source_profile="historical")

    legend_candidates = [item for item in baseline_annotation.get("hard_negatives", [])
                         if item.get("kind") == "legend_symbol"]
    if legend_id is None and entry_index is None:
        matches = legend_candidates[:1]
    elif isinstance(legend_id, str) and type(entry_index) is int and entry_index >= 0:
        matches = [item for item in legend_candidates
                   if item.get("source_reference") == f"{legend_id}:{entry_index}"]
    else:
        raise ValueError("select both legend_id and nonnegative entry_index, or neither")
    if len(matches) != 1:
        raise ValueError("selected legend glyph is absent, omitted, or ambiguous")
    target = matches[0]
    glyph_box = target["geometry"]["box"]
    center = tuple(target["geometry"]["center"])
    radius = min(glyph_box[2], glyph_box[3]) / 2.0
    target_legend_id, target_entry_text = target["source_reference"].rsplit(":", 1)
    target_entry_index = int(target_entry_text)
    legend_entries = [entry for panel in baseline_annotation["panels"]
                      for legend in panel["legends"] if legend["legend_id"] == target_legend_id
                      for entry in legend["entries"]]
    if not 0 <= target_entry_index < len(legend_entries):
        raise ValueError("selected annotation does not bind a native legend entry")
    target_entry = legend_entries[target_entry_index]
    target_shapes = [call for call in base_calls
                     if call[0] == tuple(float(value) for value in center) and
                     call[1] == float(radius) and call[2] == target_entry["shape"] and
                     call[3] == target_entry["fill"]]
    if len(target_shapes) != 1:
        raise ValueError("legend draw call is not unique by center and extent")
    target_index = base_calls.index(target_shapes[0])
    target_signature = target_shapes[0]
    if target["geometry"]["box"] is None:
        raise ValueError("legend glyph box is missing")

    ablated_calls = []
    suppressed = []

    def ablate_one_draw(draw, mask_draw, call_center, call_radius, shape, fill_state, color, stroke_width):
        index = len(ablated_calls)
        signature = _call_signature(call_center, call_radius, shape, fill_state, color, stroke_width)
        ablated_calls.append(signature)
        if index == target_index:
            if signature != target_signature:
                raise ValueError("renderer call order/signature changed during ablation")
            suppressed.append(index)
            return None
        return original_draw(draw, mask_draw, call_center, call_radius, shape, fill_state, color, stroke_width)

    with ExitStack() as stack:
        stack.enter_context(patch.object(renderer, "FontResolver", PinnedFontResolver))
        stack.enter_context(patch.object(renderer, "_draw_marker", ablate_one_draw))
        ablated_image, _, _ = render_contextual_scene(
            deepcopy(prepared), split="train", source_profile="historical")

    if len(ablated_calls) != len(base_calls) or suppressed != [target_index]:
        raise ValueError("single-glyph ablation did not suppress exactly one stable renderer call")
    if baseline_image.size != ablated_image.size:
        raise ValueError("ablation raster dimensions changed")
    rendered_rgb = baseline_image.convert("RGB").tobytes()
    ablated_rgb = ablated_image.convert("RGB").tobytes()
    source_raster_bytes = _rgb_png(baseline_image)
    scene_sha = _digest(_canonical(prepared))
    annotation_sha = _digest(_canonical(baseline_annotation))
    glyph_id = target["hard_negative_id"]
    draw_id = f"renderer_call:{target_index}"
    binding = {
        "scene_sha256": scene_sha,
        "annotation_sha256": annotation_sha,
        "source_reference": target["source_reference"],
        "glyph_id": glyph_id,
        "center": list(center),
        "glyph_box": list(glyph_box),
        "width": baseline_image.width,
        "height": baseline_image.height,
        "source_raster_sha256": _digest(source_raster_bytes),
        "rendered_rgb_sha256": _digest(rendered_rgb),
        "renderer_bundle_sha256": bundle["sha256"],
        "target_draw_id": draw_id,
        "matching_draw_ids": [draw_id],
    }
    evidence = build_visibility_evidence(
        source_raster_bytes=source_raster_bytes, rendered_rgb=rendered_rgb,
        ablated_rgb=ablated_rgb, binding=binding,
    )
    artifact = {
        "truth_id": glyph_id,
        "kind": "legend_symbol",
        "source_reference": target["source_reference"],
        "glyph_id": glyph_id,
        "center": list(center),
        "glyph_box": list(glyph_box),
        "scene_sha256": scene_sha,
        "annotation_sha256": annotation_sha,
        "visibility_evidence": evidence,
    }
    return {
        "schema": "graphreader.legend-symbol-render-ablation.v1",
        "capture_manifest_key": f"train_seed_{seed}",
        "split": "train",
        "scene_id": prepared["scene_id"],
        "scene_sha256": scene_sha,
        "annotation_sha256": annotation_sha,
        "renderer_bundle": bundle,
        "font_sha256": font_sha,
        "source_scene_sha256": _digest(_canonical(source_scene)),
        "source_raster_bytes": source_raster_bytes,
        "artifact_truth": artifact,
        "visibility_inputs": {
            "expected_binding": binding,
            "rendered_rgb": rendered_rgb,
            "ablated_rgb": ablated_rgb,
        },
        "suppressed_renderer_call_id": draw_id,
        "visible_changed_pixels": evidence["changed_pixel_count"],
        "training_approved": False,
        "optimizer_steps": 0,
    }
