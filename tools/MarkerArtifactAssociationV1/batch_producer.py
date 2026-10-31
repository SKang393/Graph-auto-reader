# SPDX-License-Identifier: Apache-2.0
"""In-memory full-V5 train source and per-legend single-draw ablations."""
from contextlib import ExitStack
from copy import deepcopy
from hashlib import sha256
from io import BytesIO
from unittest.mock import patch

from PIL import ImageChops
import numpy as np
import PIL

from .association import build_visibility_evidence
from .full_capture_producer import _canonical, _digest, verify_train_capture_bundle


def _signature(center, radius, shape, fill_state, color, stroke_width):
    return (tuple(float(value) for value in center), float(radius), str(shape), str(fill_state),
            tuple(int(value) for value in color), int(stroke_width))


def _png_v5(image):
    buffer = BytesIO()
    image.save(buffer, format="PNG", optimize=False)
    return buffer.getvalue()


def produce_v5_source_visibility(*, seed, expected_source):
    """Regenerate one bound train input and produce one ablation per native legend glyph.

    `expected_source` must come from the prevalidated report metadata binding.
    No captured raster, panel image, truth sidecar, or non-train input is read.
    """
    if PIL.__version__ != "12.3.0" or np.__version__ != "2.3.5":
        raise ValueError("V5 full-capture producer runtime differs from the pinned Python stack")
    bundle = verify_train_capture_bundle()
    from ml.synthetic import renderer, templates
    from ml.synthetic.contextual_negatives import render_contextual_scene
    from ml.synthetic.fonts import ResolvedFont
    from ml.synthetic.marker_context_catalog_v1 import labels
    from ml.synthetic.marker_context_overlay_v1.overlay import overlay
    from ml.synthetic.marker_context_train_corpus_v5 import builder, plan
    from ml.synthetic.public_fonts_v1.fonts import verify as verify_fonts

    selected = [row for row in plan.fixed_plan() if row["recipe"]["seed"] == seed]
    if len(selected) != 1 or not isinstance(expected_source, dict) or expected_source.get("seed") != seed:
        raise ValueError("seed is not one unique fixed train source")
    recipe, cue = selected[0]["recipe"], selected[0]["cue"]
    font = verify_fonts()["sans"]
    font_hash = _digest(font.read_bytes())

    class PinnedFontResolver:
        def __init__(self, *args, **kwargs):
            pass

        def resolve(self, requested, size_px):
            return ResolvedFont(str(requested), font, int(size_px), "project-local-verified",
                                font_hash, "Noto Sans", "Regular")

    base_calls = []
    original_draw = renderer._draw_marker

    def record_draw(draw, mask_draw, center, radius, shape, fill_state, color, stroke_width):
        base_calls.append(_signature(center, radius, shape, fill_state, color, stroke_width))
        return original_draw(draw, mask_draw, center, radius, shape, fill_state, color, stroke_width)

    with ExitStack() as stack:
        stack.enter_context(patch.object(templates, "FontResolver", PinnedFontResolver))
        stack.enter_context(patch.object(renderer, "FontResolver", PinnedFontResolver))
        source_scene = templates.build_scene(recipe["design"], seed, recipe["renderer_family"], panel_count=1)
        prepared = builder.prepare_scene(source_scene, recipe)
        stack.enter_context(patch.object(renderer, "_draw_marker", record_draw))
        base_image, annotation, marker_mask = render_contextual_scene(deepcopy(prepared), split="train")

    panel = prepared["panels"][0]
    x, y, width, height = panel["plot_box"]
    chosen_overlay = None
    attempts = 0
    for yy in range(int(y) + 20, int(y + height) - 20, 12):
        for xx in range(int(x) + 20, int(x + width) - 40, 12):
            if attempts >= 256:
                break
            attempts += 1
            altered, sidecar = overlay(prepared, annotation, base_image, marker_mask,
                                       recipe=recipe, panel_id=panel["panel_id"],
                                       center=(xx, yy), cue=cue)
            if sidecar.get("materialized"):
                chosen_overlay = altered, sidecar, (xx, yy)
                break
        if chosen_overlay is not None or attempts >= 256:
            break
    if chosen_overlay is None:
        raise ValueError("V5 deterministic overlay search did not materialize")
    full_image, overlay_sidecar, overlay_center = chosen_overlay
    if (overlay_sidecar.get("status") != "review_only" or
            overlay_sidecar.get("cue_visible") is not cue or
            overlay_sidecar.get("optimization_allowed") is not False):
        raise ValueError("V5 overlay scope differs")

    source_raster = _png_v5(full_image)
    scene_sha = _digest(_canonical(prepared))
    annotation_sha = _digest(_canonical(annotation))
    source_identity = {
        "seed": seed,
        "source_id": expected_source.get("source_id"),
        "scene_id": prepared["scene_id"],
        "scene_sha256": scene_sha,
        "image_sha256": _digest(source_raster),
        "width": full_image.width,
        "height": full_image.height,
    }
    if any(expected_source.get(field) != value for field, value in source_identity.items()):
        raise ValueError("regenerated V5 source identity differs from the bound capture report")
    if (full_image.size[0] <= 0 or full_image.size[1] <= 0 or
            font_hash != bundle["font_sha256"]):
        raise ValueError("regenerated V5 source dimensions or font binding differs")

    base_rgb = base_image.convert("RGB")
    full_rgb = full_image.convert("RGB")
    overlay_delta = ImageChops.difference(full_rgb, base_rgb).convert("L").point(
        lambda value: 255 if value else 0)
    glyph_items = [item for item in annotation.get("hard_negatives", [])
                   if item.get("kind") == "legend_symbol"]
    glyph_results = []
    for target in glyph_items:
        glyph_box = target["geometry"].get("box")
        center = tuple(target["geometry"].get("center", ()))
        if (not isinstance(glyph_box, list) or len(glyph_box) != 4 or len(center) != 2):
            raise ValueError("native legend glyph geometry is missing")
        target_legend_id, entry_text = target["source_reference"].rsplit(":", 1)
        entry_index = int(entry_text)
        entries = [entry for subpanel in annotation["panels"] for legend in subpanel["legends"]
                   if legend["legend_id"] == target_legend_id for entry in legend["entries"]]
        if not 0 <= entry_index < len(entries):
            raise ValueError("native legend entry reference is unresolved")
        entry = entries[entry_index]
        radius = min(glyph_box[2], glyph_box[3]) / 2.0
        matching = [call for call in base_calls if call[0] == tuple(float(v) for v in center)
                    and call[1] == float(radius) and call[2] == entry["shape"] and call[3] == entry["fill"]]
        if len(matching) != 1:
            raise ValueError("native legend symbol does not map to one renderer draw")
        target_index = base_calls.index(matching[0])
        ablated_calls, suppressed = [], []

        def ablate_draw(draw, mask_draw, call_center, call_radius, shape, fill_state, color, stroke_width):
            index = len(ablated_calls)
            signature = _signature(call_center, call_radius, shape, fill_state, color, stroke_width)
            ablated_calls.append(signature)
            if index == target_index:
                if signature != matching[0]:
                    raise ValueError("V5 renderer order changed during glyph ablation")
                suppressed.append(index)
                return None
            return original_draw(draw, mask_draw, call_center, call_radius, shape, fill_state, color, stroke_width)

        with ExitStack() as stack:
            stack.enter_context(patch.object(templates, "FontResolver", PinnedFontResolver))
            stack.enter_context(patch.object(renderer, "FontResolver", PinnedFontResolver))
            stack.enter_context(patch.object(renderer, "_draw_marker", ablate_draw))
            ablated_base, _, _ = render_contextual_scene(deepcopy(prepared), split="train")
        if len(ablated_calls) != len(base_calls) or suppressed != [target_index]:
            raise ValueError("V5 per-glyph ablation did not suppress exactly one draw")
        glyph_delta = ImageChops.difference(base_rgb, ablated_base.convert("RGB")).convert("L").point(
            lambda value: 255 if value else 0)
        if not glyph_delta.getbbox() or ImageChops.multiply(glyph_delta, overlay_delta).getbbox():
            raise ValueError("V5 glyph is invisible or overlaps the retained overlay")
        ablated_full = full_rgb.copy()
        ablated_full.paste(ablated_base.convert("RGB"), mask=glyph_delta)
        cue_box = overlay_sidecar.get("cue_box")
        if cue is True and (not isinstance(cue_box, list) or
                full_rgb.crop(tuple(cue_box)).tobytes() != ablated_full.crop(tuple(cue_box)).tobytes()):
            raise ValueError("V5 glyph ablation changed overlay cue pixels")

        binding = {
            "scene_sha256": scene_sha, "annotation_sha256": annotation_sha,
            "source_reference": target["source_reference"], "glyph_id": target["hard_negative_id"],
            "center": list(center), "glyph_box": list(glyph_box),
            "width": full_image.width, "height": full_image.height,
            "source_raster_sha256": _digest(source_raster),
            "rendered_rgb_sha256": _digest(full_rgb.tobytes()),
            "renderer_bundle_sha256": bundle["sha256"],
            "target_draw_id": f"renderer_call:{target_index}",
            "matching_draw_ids": [f"renderer_call:{target_index}"],
        }
        evidence = build_visibility_evidence(
            source_raster_bytes=source_raster,
            rendered_rgb=full_rgb.tobytes(), ablated_rgb=ablated_full.tobytes(), binding=binding)
        glyph_results.append({
            "truth_id": target["hard_negative_id"], "kind": "legend_symbol",
            "source_reference": target["source_reference"], "glyph_id": target["hard_negative_id"],
            "center": list(center), "glyph_box": list(glyph_box),
            "scene_sha256": scene_sha, "annotation_sha256": annotation_sha,
            "visibility_evidence": evidence,
            "expected_binding": binding,
        })
    if len(glyph_results) != len(glyph_items):
        raise ValueError("native legend glyph accounting is incomplete")
    classified = labels.classify(annotation, expected_split="train")
    positive_markers = [record for record in classified["records"]
                        if record["bin"] in ("connected_positive", "isolated_positive")]
    return {
        "source_identity": source_identity,
        "source_raster_sha256": _digest(source_raster),
        "annotation_sha256": annotation_sha,
        "rendered_overlay_center": list(overlay_center),
        "overlay_sidecar_sha256": _digest(_canonical(overlay_sidecar)),
        "positive_marker_points_for_suppression": [record["center"] for record in positive_markers],
        "glyph_results": glyph_results,
    }
