# SPDX-License-Identifier: Apache-2.0
"""Deterministic single-glyph ablation over one bound V5 train raster."""
from contextlib import ExitStack
from copy import deepcopy
from hashlib import sha256
from io import BytesIO
from pathlib import Path
from unittest.mock import patch
import json

from .association import build_visibility_evidence

ROOT = Path(__file__).resolve().parents[2]
EXPECTED_TRAIN_BUILDER_BINDING_SHA256 = "f095bac6f76a9a2d444869ba4d75538e76237c399d3b98997c97292b4cebebd4"
EXPECTED_CAPTURE = {
    "seed": 20820000,
    "source_id": "f9b0902a-1f41-7b53-bbef-6c3ff54bc94b",
    "scene_id": "81ce54a6-783a-53b8-9743-c4de1ea78eba",
    "scene_sha256": "ee7023484d37621b2ef7b2edfa87e4473b4246fc8ffc7451b885f71188bca7a2",
    "annotation_sha256": "706b120bbeb1487dde6bf5f16dfa74b98ecc944468793a4624bff760735913c9",
    "source_raster_sha256": "68469eda2de09632c5f7b0933c71e0c28cf86fdbcece2980b4c6f1d2168bc691",
    "width": 1200,
    "height": 350,
    "font_sha256": "b85c38ecea8a7cfb39c24e395a4007474fa5a4fc864f6ee33309eb4948d232d5",
    "overlay_center": [125, 116],
    "overlay_cue": True,
    "glyph_id": "a1c936f4-a6da-5459-b00c-f66050aff5d2",
    "source_reference": "1da52e73-44b8-517a-bbbe-d60823a31f99-legend-1:0",
    "center": [948.5, 118.5],
    "glyph_box": [941.0, 111.0, 15.0, 15.0],
}
CAPTURE_MANIFEST_KEY = "train_v5_overlay_seed_20820000"


def _canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False).encode("utf-8")


def _digest(raw):
    return sha256(raw).hexdigest()


def _safe_project_bytes(relative):
    path = ROOT
    for part in Path(relative).parts:
        path = path / part
        info = path.lstat()
        if path.is_symlink() or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ValueError("V5 train renderer source contains a link or reparse point")
    resolved = path.resolve(strict=True)
    if not resolved.is_relative_to(ROOT.resolve(strict=True)) or not resolved.is_file():
        raise ValueError("V5 train renderer source escapes the project")
    return resolved.read_bytes()


def verify_train_capture_bundle():
    """Verify the exact V5 train renderer closure without reading corpus files."""
    from ml.synthetic.marker_context_train_corpus_v5 import builder, plan
    binding = builder.source_binding()
    for relative, expected in binding["sources"].items():
        if _digest(_safe_project_bytes(relative)) != expected:
            raise ValueError(f"V5 train source changed: {relative}")
    binding_sha = plan.digest(plan.canonical(binding))
    if binding_sha != EXPECTED_TRAIN_BUILDER_BINDING_SHA256:
        raise ValueError("V5 train builder binding differs from the reviewed full-corpus identity")

    from .producer import verify_renderer_bundle
    renderer = verify_renderer_bundle()
    font_hash = binding["sources"].get("ml/synthetic/public_fonts_v1/assets/NotoSans-Regular.ttf")
    if font_hash != EXPECTED_CAPTURE["font_sha256"]:
        raise ValueError("V5 Noto Sans asset differs from the bound capture")
    bundle = {
        "schema": "graphreader.marker-v5-train-render-bundle.v1",
        "train_corpus_builder_binding_sha256": binding_sha,
        "renderer_bundle_sha256": renderer["sha256"],
        "font_sha256": font_hash,
    }
    bundle["sha256"] = _digest(_canonical(bundle))
    return bundle


def _call_signature(center, radius, shape, fill_state, color, stroke_width):
    return (tuple(float(value) for value in center), float(radius), str(shape), str(fill_state),
            tuple(int(value) for value in color), int(stroke_width))


def _capture_png(image):
    """Match V5 builder's exact PNG encoder call and defaults."""
    buffer = BytesIO()
    image.save(buffer, format="PNG", optimize=False)
    return buffer.getvalue()


def produce_v5_train_overlay_glyph_visibility(*, seed, expected_split, legend_id=None, entry_index=None):
    """Render and ablate one glyph on the exact V5 overlay-bearing train raster.

    This is limited to one fixed source identity. It generates in memory from
    the bound V5 train recipe and overlay source; it never opens corpus raster,
    truth, dev, private, or sealed files. The overlay is preserved by applying
    the single-glyph renderer delta only after proving it does not intersect the
    overlay delta.
    """
    if expected_split != "train":
        raise ValueError("V5 full-capture visibility producer is train-only")
    if type(seed) is not int or seed != EXPECTED_CAPTURE["seed"]:
        raise ValueError("seed is outside the one fixed V5 train capture")
    bundle = verify_train_capture_bundle()

    import numpy as np
    import PIL
    from PIL import ImageChops
    from ml.synthetic import renderer, templates
    from ml.synthetic.contextual_negatives import render_contextual_scene
    from ml.synthetic.fonts import ResolvedFont
    from ml.synthetic.marker_context_catalog_v1 import catalog
    from ml.synthetic.marker_context_overlay_v1.overlay import overlay
    from ml.synthetic.marker_context_train_corpus_v5 import builder, plan
    from ml.synthetic.public_fonts_v1.fonts import verify as verify_fonts

    if PIL.__version__ != "12.3.0" or np.__version__ != "2.3.5":
        raise ValueError("V5 full-capture producer runtime differs from the pinned Python stack")
    selected = [row for row in plan.fixed_plan() if row["recipe"]["seed"] == seed]
    if len(selected) != 1:
        raise ValueError("fixed V5 plan does not contain exactly one requested train seed")
    recipe, cue = selected[0]["recipe"], selected[0]["cue"]
    if recipe != {"revision": "marker-context-catalog-v1", "split": "train", "index": 0,
                  "seed": seed, "design": "ab", "connection_mode": "connected",
                  "renderer_family": "vector_clean"} or cue is not True:
        raise ValueError("V5 fixed recipe or overlay cue differs")

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
        base_calls.append(_call_signature(center, radius, shape, fill_state, color, stroke_width))
        return original_draw(draw, mask_draw, center, radius, shape, fill_state, color, stroke_width)

    with ExitStack() as stack:
        stack.enter_context(patch.object(templates, "FontResolver", PinnedFontResolver))
        stack.enter_context(patch.object(renderer, "FontResolver", PinnedFontResolver))
        source_scene = templates.build_scene(recipe["design"], seed, recipe["renderer_family"], panel_count=1)
        prepared = builder.prepare_scene(source_scene, recipe)
        stack.enter_context(patch.object(renderer, "_draw_marker", record_draw))
        base_image, annotation, marker_mask = render_contextual_scene(deepcopy(prepared), split="train")

    if legend_id is None and entry_index is None:
        matches = [item for item in annotation.get("hard_negatives", [])
                   if item.get("kind") == "legend_symbol"][:1]
    elif isinstance(legend_id, str) and type(entry_index) is int and entry_index >= 0:
        matches = [item for item in annotation.get("hard_negatives", [])
                   if item.get("kind") == "legend_symbol" and
                   item.get("source_reference") == f"{legend_id}:{entry_index}"]
    else:
        raise ValueError("select both legend_id and nonnegative entry_index, or neither")
    if len(matches) != 1:
        raise ValueError("selected V5 legend glyph is absent, omitted, or ambiguous")
    target = matches[0]
    glyph_box = target["geometry"]["box"]
    center = tuple(target["geometry"]["center"])
    target_legend_id, entry_text = target["source_reference"].rsplit(":", 1)
    target_entry_index = int(entry_text)
    legend_entries = [entry for panel in annotation["panels"] for legend in panel["legends"]
                      if legend["legend_id"] == target_legend_id for entry in legend["entries"]]
    if not 0 <= target_entry_index < len(legend_entries):
        raise ValueError("V5 annotation does not bind the selected legend entry")
    target_entry = legend_entries[target_entry_index]
    radius = min(glyph_box[2], glyph_box[3]) / 2.0
    matching_calls = [call for call in base_calls if call[0] == tuple(float(v) for v in center)
                      and call[1] == float(radius) and call[2] == target_entry["shape"]
                      and call[3] == target_entry["fill"]]
    if len(matching_calls) != 1:
        raise ValueError("selected V5 legend draw is not unique")
    target_index = base_calls.index(matching_calls[0])

    ablated_calls, suppressed = [], []

    def ablate_one_draw(draw, mask_draw, call_center, call_radius, shape, fill_state, color, stroke_width):
        index = len(ablated_calls)
        signature = _call_signature(call_center, call_radius, shape, fill_state, color, stroke_width)
        ablated_calls.append(signature)
        if index == target_index:
            if signature != matching_calls[0]:
                raise ValueError("V5 renderer draw order changed during single-glyph ablation")
            suppressed.append(index)
            return None
        return original_draw(draw, mask_draw, call_center, call_radius, shape, fill_state, color, stroke_width)

    with ExitStack() as stack:
        stack.enter_context(patch.object(templates, "FontResolver", PinnedFontResolver))
        stack.enter_context(patch.object(renderer, "FontResolver", PinnedFontResolver))
        stack.enter_context(patch.object(renderer, "_draw_marker", ablate_one_draw))
        ablated_base, _, _ = render_contextual_scene(deepcopy(prepared), split="train")
    if len(ablated_calls) != len(base_calls) or suppressed != [target_index]:
        raise ValueError("V5 ablation did not suppress exactly one stable glyph draw")

    # Match the exact builder overlay search order, without loading generated corpus images.
    panel = prepared["panels"][0]
    x, y, width, height = panel["plot_box"]
    selected_overlay = None
    attempts = 0
    for yy in range(int(y) + 20, int(y + height) - 20, 12):
        for xx in range(int(x) + 20, int(x + width) - 40, 12):
            if attempts >= 256:
                break
            attempts += 1
            altered, sidecar = overlay(prepared, annotation, base_image, marker_mask, recipe=recipe,
                                       panel_id=panel["panel_id"], center=(xx, yy), cue=cue)
            if sidecar.get("materialized"):
                selected_overlay = (altered, sidecar, (xx, yy))
                break
        if selected_overlay is not None or attempts >= 256:
            break
    if selected_overlay is None:
        raise ValueError("V5 deterministic overlay search did not materialize")
    full_image, overlay_sidecar, overlay_center = selected_overlay
    if (overlay_sidecar.get("status") != "review_only" or
            overlay_sidecar.get("cue_visible") is not cue or
            overlay_sidecar.get("optimization_allowed") is not False):
        raise ValueError("V5 overlay review-only contract differs")

    base_rgb = base_image.convert("RGB")
    ablated_base_rgb = ablated_base.convert("RGB")
    full_rgb = full_image.convert("RGB")
    glyph_delta = ImageChops.difference(base_rgb, ablated_base_rgb).convert("L").point(
        lambda value: 255 if value else 0)
    overlay_delta = ImageChops.difference(full_rgb, base_rgb).convert("L").point(
        lambda value: 255 if value else 0)
    if not glyph_delta.getbbox() or ImageChops.multiply(glyph_delta, overlay_delta).getbbox():
        raise ValueError("V5 glyph ablation is empty or overlaps the retained overlay")
    ablated_full = full_rgb.copy()
    ablated_full.paste(ablated_base_rgb, mask=glyph_delta)
    cue_box = overlay_sidecar.get("cue_box")
    if cue is True:
        if not isinstance(cue_box, list) or len(cue_box) != 4:
            raise ValueError("V5 visible cue has no bounded pixel box")
        cue_left, cue_top, cue_right, cue_bottom = cue_box
        if (full_rgb.crop((cue_left, cue_top, cue_right, cue_bottom)).tobytes() !=
                ablated_full.crop((cue_left, cue_top, cue_right, cue_bottom)).tobytes()):
            raise ValueError("single-glyph ablation changed the retained overlay cue")
    rendered_rgb = full_rgb.tobytes()
    ablated_rgb = ablated_full.tobytes()
    source_raster_bytes = _capture_png(full_image)

    scene_sha = _digest(_canonical(prepared))
    annotation_sha = _digest(_canonical(annotation))
    if (scene_sha != EXPECTED_CAPTURE["scene_sha256"] or
            annotation_sha != EXPECTED_CAPTURE["annotation_sha256"] or
            _digest(source_raster_bytes) != EXPECTED_CAPTURE["source_raster_sha256"] or
            full_image.size != (EXPECTED_CAPTURE["width"], EXPECTED_CAPTURE["height"]) or
            font_hash != EXPECTED_CAPTURE["font_sha256"] or
            list(overlay_center) != EXPECTED_CAPTURE["overlay_center"]):
        raise ValueError("Generated V5 scene, annotation, font, overlay, or raster differs from fixed train identity")
    if (target["hard_negative_id"] != EXPECTED_CAPTURE["glyph_id"] or
            target["source_reference"] != EXPECTED_CAPTURE["source_reference"] or
            list(center) != EXPECTED_CAPTURE["center"] or
            list(glyph_box) != EXPECTED_CAPTURE["glyph_box"]):
        raise ValueError("Selected legend glyph differs from the fixed capture identity")

    binding = {
        "scene_sha256": scene_sha,
        "annotation_sha256": annotation_sha,
        "source_reference": target["source_reference"],
        "glyph_id": target["hard_negative_id"],
        "center": list(center),
        "glyph_box": list(glyph_box),
        "width": full_image.width,
        "height": full_image.height,
        "source_raster_sha256": _digest(source_raster_bytes),
        "rendered_rgb_sha256": _digest(rendered_rgb),
        "renderer_bundle_sha256": bundle["sha256"],
        "target_draw_id": f"renderer_call:{target_index}",
        "matching_draw_ids": [f"renderer_call:{target_index}"],
    }
    evidence = build_visibility_evidence(source_raster_bytes=source_raster_bytes,
                                         rendered_rgb=rendered_rgb,
                                         ablated_rgb=ablated_rgb,
                                         binding=binding)
    artifact = {"truth_id": target["hard_negative_id"], "kind": "legend_symbol",
                "source_reference": target["source_reference"], "glyph_id": target["hard_negative_id"],
                "center": list(center), "glyph_box": list(glyph_box),
                "scene_sha256": scene_sha, "annotation_sha256": annotation_sha,
                "visibility_evidence": evidence}
    return {
        "schema": "graphreader.legend-symbol-v5-overlay-ablation.v1",
        "capture_manifest_key": CAPTURE_MANIFEST_KEY,
        "split": "train",
        "source_id": EXPECTED_CAPTURE["source_id"],
        "scene_id": prepared["scene_id"],
        "scene_sha256": scene_sha,
        "annotation_sha256": annotation_sha,
        "renderer_bundle": bundle,
        "font_sha256": font_hash,
        "source_raster_bytes": source_raster_bytes,
        "artifact_truth": artifact,
        "visibility_inputs": {"expected_binding": binding, "rendered_rgb": rendered_rgb,
                               "ablated_rgb": ablated_rgb},
        "suppressed_renderer_call_id": f"renderer_call:{target_index}",
        "visible_changed_pixels": evidence["changed_pixel_count"],
        "overlay": {"cue": cue, "center": list(overlay_center),
                    "cue_box": cue_box,
                    "sidecar_sha256": _digest(_canonical(overlay_sidecar)),
                    "preserved_pixel_difference_intersection": False},
        "source_binding": {"train_corpus_builder_binding_sha256": bundle["train_corpus_builder_binding_sha256"],
                           "renderer_bundle_sha256": bundle["renderer_bundle_sha256"]},
    }
