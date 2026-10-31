# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Generate bounded train-only panel crops seeded by measured x-axis strokes.

This candidate crop recipe is intentionally distinct from production recovery:
production associates detected glyph components with supplied strokes, while
this materializer uses a stroke only to place a fixed-size coarse crop window.
"""

from __future__ import annotations

import argparse
from hashlib import sha256
from io import BytesIO
import json
import math
from pathlib import Path
import random
import sys
from typing import Any, Mapping

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT))
from ml.synthetic import renderer as synthetic_renderer, templates as synthetic_templates
from ml.synthetic.fonts import ResolvedFont
from ml.synthetic.renderer import _apply_matrix, _warp_mask, render_scene
from ml.synthetic.templates import build_scene


FAMILY = "stroke-seeded-xtick-v1"
OUTPUT = ROOT / "artifacts" / "stroke-seeded-xtick-v1-train-verified-v6"
CROP_WIDTH, CROP_HEIGHT = 48, 48
INPUT_WIDTH, INPUT_HEIGHT = 256, 96
MAX_SOURCES, MAX_PANELS, MAX_CROPS = 24, 72, 900
SEED_BASE = 27103100
DESIGNS = ("ab", "aba", "abab", "multiple_baseline", "multiple_probe")
FONTS = ("vector_clean", "print_monochrome", "scan_rough", "hand_drawn")
FONT_SIZES = (10, 12, 14, 16, 18, 20, 22, 24)
LOCAL_FONTS = {
    "regular": ROOT / "src" / "GraphReader.App" / "Assets" / "Fonts" / "NotoSans-Regular.ttf",
    "medium": ROOT / "src" / "GraphReader.App" / "Assets" / "Fonts" / "NotoSans-Medium.ttf",
    "semibold": ROOT / "src" / "GraphReader.App" / "Assets" / "Fonts" / "NotoSans-SemiBold.ttf",
}


class MaterializationError(RuntimeError):
    pass


class _ProjectFontResolver:
    """Resolve synthetic font requests only to project-local font assets."""

    def __init__(self, *_: Any) -> None:
        pass

    def resolve(self, requested: str | None, size_px: int) -> ResolvedFont:
        style = {"serif": "medium", "monospace": "semibold", "mono": "semibold", "handwritten": "regular"}.get(
            str(requested).casefold() if requested else "sans", "regular")
        path = LOCAL_FONTS[style]
        if not path.is_file() or path.is_symlink() or not path.resolve().is_relative_to(ROOT.resolve()):
            raise MaterializationError("required project-local font is missing or unsafe")
        font = ImageFont.truetype(str(path), size_px)
        family, face = font.getname()
        return ResolvedFont(str(path), path, size_px, "project_local", digest(path.read_bytes()), family, face)


def render_train_scene(scene: dict[str, Any]):
    """Render with project fonts without probing host font directories."""
    old_templates, old_renderer = synthetic_templates.FontResolver, synthetic_renderer.FontResolver
    synthetic_templates.FontResolver = _ProjectFontResolver
    synthetic_renderer.FontResolver = _ProjectFontResolver
    try:
        return render_scene(scene)
    finally:
        synthetic_templates.FontResolver = old_templates
        synthetic_renderer.FontResolver = old_renderer


def digest(data: bytes) -> str:
    return sha256(data).hexdigest()


def canonical_json(value: object) -> bytes:
    return json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(",", ":")).encode("utf-8")


def source_recipes() -> tuple[dict[str, Any], ...]:
    """A deterministic, generated-only train source inventory."""
    rng = random.Random(SEED_BASE)
    rows = []
    for index in range(MAX_SOURCES):
        seed = SEED_BASE + index
        renderer_family = FONTS[(index // 2) % len(FONTS)]
        # These fixed recipes select perspective warps; this materializer
        # deliberately rejects non-affine geometry rather than approximating it.
        if index in {7, 22, 23}:
            renderer_family = "scan_rough"
        row = {
            "source_id": f"{FAMILY}-train-{seed}",
            "seed": seed,
            "split": "train",
            "design": DESIGNS[index % len(DESIGNS)],
            "renderer_family": renderer_family,
            "panel_count": 1 + (index % 3),
            "session_count": 7 + rng.randrange(12),
            "canvas_width": 900 + 100 * (index % 10),
            "panel_height": 210 + 20 * (index % 8),
            "font_size_px": FONT_SIZES[index % len(FONT_SIZES)],
            "x_label_visibility": ("visible", "partial", "hidden")[index % 3],
            "split_role": "train",
        }
        row["source_identity_sha256"] = digest(canonical_json(row))
        rows.append(row)
    return tuple(rows)


def build_train_scene(recipe: Mapping[str, Any]) -> dict[str, Any]:
    if recipe.get("split_role") != "train" or recipe.get("split") != "train":
        raise MaterializationError("only train source recipes are permitted")
    old_resolver = synthetic_templates.FontResolver
    synthetic_templates.FontResolver = _ProjectFontResolver
    try:
        return build_scene(
            str(recipe["design"]), int(recipe["seed"]), str(recipe["renderer_family"]),
            int(recipe["panel_count"]), session_count=int(recipe["session_count"]),
            canvas_width=int(recipe["canvas_width"]), panel_height=int(recipe["panel_height"]),
            presentation={"font_size_px": int(recipe["font_size_px"]), "dense_tick_labels": True,
                          "x_label_visibility": str(recipe["x_label_visibility"])},
        )
    finally:
        synthetic_templates.FontResolver = old_resolver


def _glyph_alpha_mask(record: Mapping[str, Any], font: ImageFont.FreeTypeFont, size: tuple[int, int]) -> Image.Image:
    """Reproduce the renderer's unrotated label alpha raster to derive tight pixels."""
    text = str(record["text"])
    x, y = (float(v) for v in record["position"])
    draw_probe = ImageDraw.Draw(Image.new("L", (1, 1), 0))
    nominal = draw_probe.textbbox((x, y), text, font=font, anchor="lt")
    padding = 3
    ox, oy = int(nominal[0] // 1) - padding, int(nominal[1] // 1) - padding
    width = max(1, int(-(-nominal[2] // 1)) - ox + padding)
    height = max(1, int(-(-nominal[3] // 1)) - oy + padding)
    layer = Image.new("RGBA", (width, height), (255, 255, 255, 0))
    ImageDraw.Draw(layer).text((x - ox, y - oy), text, font=font, fill=(0, 0, 0, 255), anchor="lt")
    mask = Image.new("L", size, 0)
    mask.paste(layer.getchannel("A"), (ox, oy))
    return mask


def _crop_and_resize(image: Image.Image, center_x: float, baseline_y: float) -> tuple[Image.Image, dict[str, Any]]:
    """Extract a 96x48 original-pixel window, then OCR-style aspect-preserving pad.

    It mirrors OcrCropBatcher defaults for horizontal text: 1-pixel crop padding
    is replaced here by a stroke-lane proposal window; resize preserves aspect
    ratio and leaves neutral padding on the right. The affine source mapping is
    exact over the unpadded 192x96 content.
    """
    left, top = int(round(center_x - CROP_WIDTH / 2)), int(round(baseline_y))
    source = Image.new("L", (CROP_WIDTH, CROP_HEIGHT), 255)
    ix0, iy0 = max(0, left), max(0, top)
    ix1, iy1 = min(image.width, left + CROP_WIDTH), min(image.height, top + CROP_HEIGHT)
    if ix1 > ix0 and iy1 > iy0:
        source.paste(image.crop((ix0, iy0, ix1, iy1)), (ix0 - left, iy0 - top))
    content_width = int(INPUT_HEIGHT * CROP_WIDTH / CROP_HEIGHT)
    resized = source.resize((content_width, INPUT_HEIGHT), Image.Resampling.BILINEAR)
    output = Image.new("L", (INPUT_WIDTH, INPUT_HEIGHT), 128)
    output.paste(resized, (0, 0))
    transform = {
        "original_to_crop_pixel": [INPUT_HEIGHT / CROP_WIDTH, 0.0, -left * INPUT_HEIGHT / CROP_WIDTH,
                                   0.0, INPUT_HEIGHT / CROP_HEIGHT, -top * INPUT_HEIGHT / CROP_HEIGHT],
        "crop_pixel_to_original": [CROP_WIDTH / INPUT_HEIGHT, 0.0, float(left),
                                   0.0, CROP_HEIGHT / INPUT_HEIGHT, float(top)],
        "source_window_xywh": [left, top, CROP_WIDTH, CROP_HEIGHT],
        "source_image_size": [image.width, image.height],
        "content_xywh": [0, 0, content_width, INPUT_HEIGHT],
        "canvas_size": [INPUT_WIDTH, INPUT_HEIGHT],
        "outside_panel_value": 255,
    }
    return output, transform


def _map_box(box_xyxy: tuple[float, float, float, float], transform: Mapping[str, Any]) -> list[float] | None:
    left, top, right, bottom = box_xyxy
    sx, _, tx, _, sy, ty = transform["original_to_crop_pixel"]
    cx0, cy0, cw, ch = transform["content_xywh"]
    x0, x1 = left * sx + tx, right * sx + tx
    y0, y1 = top * sy + ty, bottom * sy + ty
    x0, x1 = max(cx0, x0), min(cx0 + cw, x1)
    y0, y1 = max(cy0, y0), min(cy0 + ch, y1)
    if x1 <= x0 or y1 <= y0:
        return None
    return [x0, y0, x1, y1]


def _apply_box(matrix: list[float], box: tuple[float, float, float, float]) -> tuple[float, float, float, float]:
    x0, y0, x1, y1 = box
    points = [_apply_matrix(matrix, p) for p in ((x0, y0), (x1, y0), (x1, y1), (x0, y1))]
    return min(p[0] for p in points), min(p[1] for p in points), max(p[0] for p in points), max(p[1] for p in points)


def _map_line(line: list[list[float]], matrix: list[float] | None) -> list[float]:
    points = [tuple(float(v) for v in point) for point in line]
    if matrix is not None:
        points = [_apply_matrix(matrix, point) for point in points]
    return [*points[0], *points[1]]


def make_crop_example(image: Image.Image, center_x: float, baseline_y: float,
                      label_boxes: list[tuple[str, tuple[float, float, float, float]]]) -> tuple[Image.Image, list[dict[str, Any]]]:
    """Create a crop and all glyph targets intersecting its source window."""
    crop, transform = _crop_and_resize(image, center_x, baseline_y)
    left, top, width, height = transform["source_window_xywh"]
    targets = []
    for text, box in label_boxes:
        if box[0] < left + width and box[2] > left and box[1] < top + height and box[3] > top:
            mapped = _map_box(box, transform)
            if mapped is not None:
                targets.append({"text": text, "glyph_mask_xyxy": list(box), "target_crop_xyxy": mapped})
    return crop, targets


def classify_crop(expected_label: str | None, target_texts: list[str], *,
                  expected_found: bool, expected_fully_inside: bool,
                  non_tick_text: bool, any_tick_clipped: bool) -> tuple[str, list[str]]:
    """Admit only a text-free negative or one complete seeded XTick positive."""
    reasons = []
    if expected_label is not None:
        if not expected_found:
            reasons.append("visible_tick_label_unmatched_or_outside_crop")
        elif not expected_fully_inside:
            reasons.append("visible_tick_label_clipped_by_crop_window")
    if non_tick_text:
        reasons.append("non_xtick_text_intersects_crop")
    if any_tick_clipped:
        reasons.append("x_tick_glyph_clipped_by_crop_window")
    if len(target_texts) > 1:
        reasons.append("multiple_xtick_targets_in_crop")
    if expected_label is None and target_texts:
        reasons.append("unseeded_neighbor_tick_text_in_crop")
    if expected_label is not None and len(target_texts) == 1 and target_texts[0] != expected_label:
        reasons.append("target_label_does_not_match_seeded_tick")
    reasons = list(dict.fromkeys(reasons))
    return ("excluded" if reasons else "positive" if target_texts else "background_only", reasons)


def materialize_recipe(recipe: Mapping[str, Any]) -> list[dict[str, Any]]:
    scene = build_train_scene(recipe)
    panel_image, rendered, _ = render_train_scene(scene)
    final_forward = rendered["transforms"][-1]["forward_matrix_3x3"] if rendered["transforms"] else None
    if final_forward is not None and (len(final_forward) != 9 or not all(isinstance(n, (int, float)) and math.isfinite(n) for n in final_forward)
                                      or abs(final_forward[6]) > 1e-12 or abs(final_forward[7]) > 1e-12
                                      or abs(final_forward[8]) < 1e-12
                                      or abs(final_forward[0] * final_forward[4] - final_forward[1] * final_forward[3]) < 1e-12):
        raise MaterializationError("non-affine or ambiguous renderer transforms are not admitted")
    source_id = str(recipe["source_id"])
    source_sha = digest(panel_image.tobytes())
    results: list[dict[str, Any]] = []
    for panel_index, panel in enumerate(rendered["panels"]):
        source_ticks = {tick["tick_id"]: tick for tick in scene["panels"][panel_index].get("ticks", [])
                        if tick.get("axis", "x") == "x"}
        ticks = [t for t in panel["ticks"] if t.get("axis") == "x" and t.get("role") == "x_tick"]
        text_records = [t for t in panel["texts"] if t.get("visible") and t.get("rendered_pixel_box")]
        for stroke_index, stroke in enumerate(ticks):
            stroke_center = tuple(float(x) for x in stroke["center"])
            if final_forward is not None:
                stroke_center = _apply_matrix(final_forward, stroke_center)
            cx, baseline = stroke_center
            gray_panel = panel_image.convert("L")
            font_info = rendered["font"]
            font = ImageFont.truetype(font_info["resolved_path"], int(font_info["size_px"]))
            glyphs = []
            for record in text_records:
                box = record["rendered_pixel_box"]
                x0, y0, w, h = (float(v) for v in box)
                mask = _glyph_alpha_mask(record, font, panel_image.size)
                if final_forward is not None:
                    mask = _warp_mask(mask, final_forward)
                actual = mask.getbbox()
                expected = (x0, y0, x0 + w, y0 + h)
                if record.get("role") == "x_tick" and final_forward is None and (
                        actual is None or any(abs(a - b) > 1e-6 for a, b in zip(actual, expected))):
                    raise MaterializationError("glyph mask disagrees with renderer's actual alpha bounds")
                if record.get("role") == "x_tick" and final_forward is not None:
                    expected_mapped = _apply_box(final_forward, expected)
                    if actual is None or any(abs(a - b) > 5.0 for a, b in zip(actual, expected_mapped)):
                        raise MaterializationError("warped glyph mask disagrees with transformed renderer bounds")
                if actual is not None:
                    glyphs.append((str(record["text"]), tuple(actual), mask, record))
            crop, transform = _crop_and_resize(gray_panel, cx, baseline - 2)
            left, top, width, height = transform["source_window_xywh"]
            crop_glyphs = []
            candidate_bounds: dict[str, tuple[float, float, float, float]] = {}
            non_tick_text_intersects = False
            x_tick_glyph_clipped = False
            for text, box, mask, record in glyphs:
                if box[0] < left + width and box[2] > left and box[1] < top + height and box[3] > top:
                    if record.get("role") != "x_tick":
                        non_tick_text_intersects = True
                        continue
                    if not (left <= box[0] and box[2] <= left + width and top <= box[1] and box[3] <= top + height):
                        x_tick_glyph_clipped = True
                    mapped = _map_box(box, transform)
                    if mapped is not None:
                        crop_glyphs.append({"text": text, "glyph_mask_xyxy": list(box), "target_crop_xyxy": mapped,
                                            "glyph_pixel_count": sum(v > 0 for v in mask.tobytes()),
                                            "glyph_mask_sha256": digest(mask.tobytes()), "text_region_id": record["region_id"]})
                        candidate_bounds[text] = box
            source_tick = source_ticks.get(stroke["tick_id"])
            expected_label = source_tick.get("label") if source_tick is not None else None
            expected_box = candidate_bounds.get(str(expected_label)) if expected_label is not None else None
            expected_found = expected_box is not None
            expected_fully_inside = bool(expected_box is not None and left <= expected_box[0]
                                         and expected_box[2] <= left + width and top <= expected_box[1]
                                         and expected_box[3] <= top + height)
            kind, exclusion_reasons = classify_crop(
                str(expected_label) if expected_label is not None else None,
                [target["text"] for target in crop_glyphs],
                expected_found=expected_found, expected_fully_inside=expected_fully_inside,
                non_tick_text=non_tick_text_intersects, any_tick_clipped=x_tick_glyph_clipped)
            exclusion_reason = exclusion_reasons[0] if exclusion_reasons else None
            row = {
                "schema": "graphreader.stroke-seeded-xtick-v1-crop.v1",
                "source_id": source_id, "source_identity_sha256": recipe["source_identity_sha256"],
                "source_sha256": source_sha, "source_panel_index": panel_index,
                "source_panel_id": panel["panel_id"], "split": "train", "case_id": f"{source_id}-p{panel_index}-t{stroke_index}",
                "tick_id": stroke["tick_id"], "stroke_center_original_xy": list(stroke_center),
                "stroke_line_original_xyxy": _map_line(stroke["line"], final_forward),
                "renderer_transform_applied_to_nested_geometry": final_forward is not None,
                "rendered_font": rendered["font"],
                "expected_tick_label": expected_label, "exclusion_reason": exclusion_reason,
                "exclusion_reasons": exclusion_reasons,
                "trainable_single_box": kind in {"positive", "background_only"},
                "crop_transform": transform,
                "targets": crop_glyphs, "kind": kind,
                "crop_pixels": crop.tobytes(),
            }
            results.append(row)
    return results


def materialize(output: Path = OUTPUT) -> dict[str, Any]:
    if output.exists() or output.is_symlink():
        raise MaterializationError("output path must be new")
    if output.resolve().is_relative_to(ROOT.resolve()) is False:
        raise MaterializationError("output must stay inside this project")
    recipes = source_recipes()
    if len(recipes) > MAX_SOURCES:
        raise MaterializationError("source count exceeds the bound")
    output.mkdir(parents=True)
    (output / "images").mkdir()
    (output / "annotations").mkdir()
    rows, sources = [], []
    for recipe in recipes:
        crops = materialize_recipe(recipe)
        sources.append({"source_id": recipe["source_id"], "source_identity_sha256": recipe["source_identity_sha256"],
                        "seed": recipe["seed"], "panel_count": recipe["panel_count"], "split": "train",
                        "recipe": recipe, "crop_count": len(crops),
                        "font_provenance": crops[0]["rendered_font"] if crops else None})
        for row in crops:
            if len(rows) >= MAX_CROPS:
                raise MaterializationError("crop count exceeds the bound")
            pixels = row.pop("crop_pixels")
            image = Image.frombytes("L", (INPUT_WIDTH, INPUT_HEIGHT), pixels)
            buffer = BytesIO()
            image.save(buffer, format="PNG", compress_level=9)
            image_bytes = buffer.getvalue()
            row["image_path"] = f"images/{row['case_id']}.png"
            row["image_sha256"] = digest(image_bytes)
            row["annotation_path"] = f"annotations/{row['case_id']}.json"
            ann_bytes = canonical_json(row)
            row["annotation_sha256"] = digest(ann_bytes)
            (output / row["image_path"]).write_bytes(image_bytes)
            (output / row["annotation_path"]).write_bytes(ann_bytes)
            rows.append({k: row[k] for k in ("case_id", "source_id", "source_identity_sha256", "split", "kind",
                "trainable_single_box", "image_path", "image_sha256", "annotation_path", "annotation_sha256")})
        if sum(int(s["panel_count"]) for s in sources) > MAX_PANELS:
            raise MaterializationError("panel count exceeds the bound")
    manifest = {
        "schema": "graphreader.stroke-seeded-xtick-v1-train.v1", "family": FAMILY, "split": "train",
        "generator_sha256": digest(Path(__file__).read_bytes()), "record_count": len(rows),
        "source_count": len(sources), "positive_count": sum(r["kind"] == "positive" for r in rows),
        "background_only_count": sum(r["kind"] == "background_only" for r in rows),
        "excluded_count": sum(r["kind"] == "excluded" for r in rows),
        "trainable_single_box_positive_count": sum(r["kind"] == "positive" and r["trainable_single_box"] for r in rows),
        "trainable_single_box_background_count": sum(r["kind"] == "background_only" and r["trainable_single_box"] for r in rows),
        "crop_canvas": [INPUT_WIDTH, INPUT_HEIGHT], "source_window": [CROP_WIDTH, CROP_HEIGHT],
        "recipe_bounds": {"max_sources": MAX_SOURCES, "max_panels": MAX_PANELS, "max_crops": MAX_CROPS},
        "crop_contract": "stroke-seeded coarse crop; preserve aspect ratio; right-pad neutral; source transform explicit",
        "production_relation": "Production TickLaneTextRegionRecovery associates connected-component glyph boxes with strokes. This materializer proposes a new coarse crop window and does not claim production uses it.",
        "sources": sources, "records": rows, "probe_dev_sealed_private_inputs_read": False,
    }
    data = canonical_json(manifest)
    (output / "manifest.json").write_bytes(data)
    return {"output": output.relative_to(ROOT).as_posix(), "manifest_sha256": digest(data),
            "sources": len(sources), "crops": len(rows), "positive": manifest["positive_count"],
            "background_only": manifest["background_only_count"], "excluded": manifest["excluded_count"],
            "trainable_single_box_positive": manifest["trainable_single_box_positive_count"],
            "trainable_single_box_background": manifest["trainable_single_box_background_count"]}


def verify_corpus(output: Path = OUTPUT) -> dict[str, Any]:
    resolved_root = ROOT.resolve(strict=True)
    resolved = output.resolve(strict=True)
    if not resolved.is_relative_to(resolved_root) or output.is_symlink():
        raise MaterializationError("corpus path escapes project root or is a symlink")
    manifest_path = resolved / "manifest.json"
    manifest_bytes = manifest_path.read_bytes()
    manifest = json.loads(manifest_bytes)
    if (manifest.get("schema") != "graphreader.stroke-seeded-xtick-v1-train.v1"
            or manifest.get("split") != "train"
            or manifest.get("probe_dev_sealed_private_inputs_read") is not False
            or len(manifest.get("sources", [])) != MAX_SOURCES
            or len(manifest.get("records", [])) != manifest.get("record_count")):
        raise MaterializationError("corpus manifest scope or counts differ")
    source_ids = {source["source_id"] for source in manifest["sources"] if source.get("split") == "train"}
    if len(source_ids) != MAX_SOURCES:
        raise MaterializationError("source identity inventory is incomplete")
    counts: dict[str, int] = {}
    for record in manifest["records"]:
        if record.get("split") != "train" or record.get("source_id") not in source_ids:
            raise MaterializationError("non-train or ungrouped crop in corpus")
        counts[record["source_id"]] = counts.get(record["source_id"], 0) + 1
        for relative, expected in ((record["image_path"], record["image_sha256"]),
                                   (record["annotation_path"], record["annotation_sha256"])):
            path = resolved / relative
            if path.is_symlink() or not path.resolve(strict=True).is_relative_to(resolved):
                raise MaterializationError("artifact path escapes corpus root")
            payload = path.read_bytes()
            if digest(payload) != expected:
                raise MaterializationError("emitted artifact checksum mismatch")
            if relative == record["image_path"]:
                with Image.open(BytesIO(payload)) as image:
                    if image.mode != "L" or image.size != (INPUT_WIDTH, INPUT_HEIGHT):
                        raise MaterializationError("crop image mode or dimensions differ")
            else:
                annotation = json.loads(payload)
                if annotation.get("case_id") != record["case_id"] or annotation.get("source_id") != record["source_id"]:
                    raise MaterializationError("crop annotation identity differs")
                if annotation.get("kind") == "positive" and not annotation.get("targets"):
                    raise MaterializationError("positive crop has no targets")
                if annotation.get("kind") == "background_only" and annotation.get("targets"):
                    raise MaterializationError("background-only crop has targets")
                if (annotation.get("kind") == "excluded") != bool(annotation.get("exclusion_reason")):
                    raise MaterializationError("crop exclusion is not explicitly recorded")
                if (annotation.get("kind") == "excluded") != bool(annotation.get("exclusion_reasons")):
                    raise MaterializationError("crop exclusion reasons are incomplete")
                if annotation.get("kind") == "positive" and (
                        not annotation.get("trainable_single_box") or len(annotation.get("targets", [])) != 1
                        or annotation.get("targets", [])[0]["text"] != annotation.get("expected_tick_label")):
                    raise MaterializationError("positive is unsafe for the one-box detector")
                if annotation.get("kind") == "excluded" and annotation.get("trainable_single_box"):
                    raise MaterializationError("excluded crop is marked trainable")
                for target in annotation.get("targets", []):
                    x0, y0, x1, y1 = target["target_crop_xyxy"]
                    if not (0 <= x0 < x1 <= INPUT_WIDTH and 0 <= y0 < y1 <= INPUT_HEIGHT):
                        raise MaterializationError("target box leaves crop canvas")
    if len(counts) != MAX_SOURCES:
        raise MaterializationError("one or more source groups have no crops")
    return {"manifest_sha256": digest(manifest_bytes), "sources": len(source_ids),
            "crops": manifest["record_count"], "positive": manifest["positive_count"],
            "background_only": manifest["background_only_count"], "excluded": manifest["excluded_count"],
            "checked_artifacts": 2 * manifest["record_count"]}


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, default=OUTPUT)
    parser.add_argument("--verify", action="store_true")
    args = parser.parse_args()
    result = verify_corpus(args.output) if args.verify else materialize(args.output)
    print(json.dumps(result, sort_keys=True, indent=2))
