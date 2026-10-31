# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Materialize a family-disjoint public corpus of rendered numeric X-tick crops.

This module deliberately uses only the existing ``real_range`` generator.  It
never requests the reserved hand-drawn family, private inputs, or sealed data,
and it does not construct a model or optimizer.
"""
from __future__ import annotations

from collections import Counter
from hashlib import sha256
from importlib.metadata import version
import json
import math
from pathlib import Path
from tempfile import TemporaryDirectory

from PIL import Image, ImageDraw, ImageFont

from ml.synthetic.dataset import FAMILY_AXES, PRESETS, _build_scenes, generate_dataset
from ml.synthetic.io import canonical_json_bytes, png_bytes

ROOT = Path(__file__).resolve().parents[3]
OUTPUT_SCHEMA = "graphreader.synthetic-tick-corpus.v2"
PRESET = "real_range"
PREFLIGHT = ROOT / "artifacts/synthetic-tick-corpus-v2-full-gate-preflight-v1.json"
PREFLIGHT_SHA256 = "9f445784f8475134156f9e0811d7e443175348c0b1789a4ec2266048d8a5114f"
# Explicit rather than range-derived so a corpus identity never changes by an
# accidental edit to a loop bound.
SEEDS = (
    20261040, 20261044, 20261048, 20261050, 20261051, 20261053,
    20261054, 20261055, 20261057, 20261058, 20261059, 20261060,
    20261063, 20261064, 20261066, 20261068, 20261070, 20261071,
    20261073, 20261074, 20261075, 20261076, 20261077, 20261078,
)


def _digest(data: bytes) -> str:
    return sha256(data).hexdigest()


def _json(raw: bytes) -> dict:
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise ValueError("duplicate JSON key")
            result[key] = value
        return result
    value = json.loads(raw, object_pairs_hook=pairs)
    if not isinstance(value, dict):
        raise ValueError("expected JSON object")
    return value


def _source_hashes() -> dict[str, str]:
    paths = sorted((ROOT / "ml/synthetic").glob("*.py"))
    paths += [ROOT / "ml/synthetic/scene.schema.json", Path(__file__).resolve()]
    return {path.relative_to(ROOT).as_posix(): _digest(path.read_bytes()) for path in paths}


def _preflight_binding() -> dict:
    raw = PREFLIGHT.read_bytes()
    if _digest(raw) != PREFLIGHT_SHA256:
        raise ValueError("full-gate preflight hash differs")
    report = _json(raw)
    required = {"schema": "graphreader.synthetic-tick-corpus.full-gate-preflight.v1", "preset": PRESET,
                "selected_seeds": list(SEEDS), "full_generator_distribution_gate_passed": True,
                "synthetic_only": True, "private_reads": 0, "sealed_reads": 0, "optimizer_steps": 0,
                "reserved_test_family_opened": False}
    if any(report.get(key) != value for key, value in required.items()):
        raise ValueError("full-gate preflight scope differs")
    return {"path": PREFLIGHT.relative_to(ROOT).as_posix(), "sha256": PREFLIGHT_SHA256}


def _require_public_train_validation_only() -> None:
    """Prove the selected preset constructs no reserved test-family scenes."""
    if PRESET not in PRESETS or len(SEEDS) != 24 or len(set(SEEDS)) != len(SEEDS):
        raise ValueError("frozen public corpus configuration differs")
    for seed in SEEDS:
        scenes = _build_scenes(PRESETS[PRESET], seed, require_complete_style_catalog=False)
        if len(scenes) != 10:
            raise ValueError("real_range case count differs")
        for scene in scenes:
            families = scene.get("families")
            if not isinstance(families, dict) or set(families) != set(FAMILY_AXES):
                raise ValueError("scene family axes differ")
            splits = {item.get("split") for item in families.values() if isinstance(item, dict)}
            keys = {item.get("key") for item in families.values() if isinstance(item, dict)}
            if len(splits) != 1 or not splits <= {"train", "validation"} or "hand_drawn" in keys:
                raise ValueError("selected preset is not public train/validation only")


def _integer_box(box, width: int, height: int) -> tuple[int, int, int, int]:
    if (not isinstance(box, list) or len(box) != 4 or
            any(type(value) not in (int, float) or not math.isfinite(value) for value in box)):
        raise ValueError("rendered X-tick box is invalid")
    x, y, box_width, box_height = box
    left, top = math.floor(x), math.floor(y)
    right, bottom = math.ceil(x + box_width), math.ceil(y + box_height)
    if box_width <= 0 or box_height <= 0 or left < 0 or top < 0 or right > width or bottom > height or left >= right or top >= bottom:
        raise ValueError("rendered X-tick box escapes source image")
    return left, top, right, bottom


def _font_sha(annotation: dict) -> str:
    font = annotation.get("font")
    value = font.get("sha256") if isinstance(font, dict) else None
    if (not isinstance(value, str) or len(value) != 64 or
            any(character not in "0123456789abcdef" for character in value)):
        raise ValueError("rendered font provenance differs")
    return value


def _glyph_visibility(scene: dict, annotation: dict, image: Image.Image, record: dict) -> tuple[str | None, str | None]:
    """Bind a retained crop to the generator's actual text alpha layer.

    The final raster must retain the source glyph's alpha support.  Merely
    contrasting with a canvas corner is insufficient because a line or block
    can occupy a text box after rendering.
    """
    font = annotation["font"]
    font_path = Path(font.get("resolved_path", ""))
    if not font_path.is_file() or _digest(font_path.read_bytes()) != _font_sha(annotation):
        raise ValueError("rendered font file differs from provenance")
    size = font.get("size_px")
    if type(size) is not int or size <= 0:
        raise ValueError("rendered font size differs from provenance")
    regions = {item.get("region_id"): item for item in scene["annotations"]["text_regions"]}
    source = regions.get(record["region_id"])
    if not isinstance(source, dict):
        raise ValueError("X-tick source region differs")
    position = (float(source["box"][0]), float(source["box"][1]))
    text = str(source["text"])
    font_object = ImageFont.truetype(str(font_path), size)
    measurement = ImageDraw.Draw(Image.new("RGB", (1, 1))).textbbox(position, text, font=font_object, anchor="lt")
    padding = 3
    origin_x, origin_y = math.floor(measurement[0]) - padding, math.floor(measurement[1]) - padding
    layer = Image.new("RGBA", (max(1, math.ceil(measurement[2]) - origin_x + padding),
                                max(1, math.ceil(measurement[3]) - origin_y + padding)), (255, 255, 255, 0))
    ImageDraw.Draw(layer).text((position[0] - origin_x, position[1] - origin_y), text, font=font_object,
                               fill=(0, 0, 0, 255), anchor="lt")
    # This is the alpha before production's RGB paste.  Pasting an RGBA layer
    # into transparent RGBA with itself as mask squares edge alpha and is not
    # an equivalent source mask.
    alpha_layer = layer.getchannel("A")
    alpha_box = alpha_layer.getbbox()
    if alpha_box is None:
        raise ValueError("renderer emitted an empty X-tick glyph alpha layer")
    expected_box = [float(origin_x + alpha_box[0]), float(origin_y + alpha_box[1]),
                    float(alpha_box[2] - alpha_box[0]), float(alpha_box[3] - alpha_box[1])]
    if expected_box != record["rendered_pixel_box"]:
        raise ValueError("rendered X-tick alpha box differs from generator source")
    left, top, right, bottom = record["crop_xyxy"]
    alpha = Image.new("L", image.size, 0)
    alpha.paste(alpha_layer, (origin_x, origin_y))
    alpha = alpha.crop((left, top, right, bottom))
    expected = [value >= 16 for value in alpha.get_flattened_data()]
    expected_count = sum(expected)
    if expected_count == 0:
        raise ValueError("renderer emitted an empty X-tick glyph alpha layer")
    crop = image.convert("RGB").crop((left, top, right, bottom))
    background = image.convert("RGB").getpixel((0, 0))
    actual = [max(abs(channel - base) for channel, base in zip(pixel, background, strict=True)) >= 24
              for pixel in crop.get_flattened_data()]
    actual_count = sum(actual)
    overlap = sum(left_value and right_value for left_value, right_value in zip(expected, actual, strict=True))
    # This retains anti-aliased glyphs but rejects a full contrasting block or
    # line that covers the label box without its expected glyph topology.
    if overlap / expected_count < 0.90 or overlap / max(1, actual_count) < 0.67:
        return None, "renderer_glyph_alpha_not_visible_in_final_crop"
    return _digest(alpha.tobytes()), None


def _x_tick_records(scene: dict, annotation: dict, image: Image.Image) -> list[dict]:
    if (annotation.get("scene_id") != scene.get("scene_id") or annotation.get("coordinate_space") != "original_pixels" or
            annotation.get("canvas", {}).get("width") != image.width or annotation.get("canvas", {}).get("height") != image.height):
        raise ValueError("scene, annotation, and image identity differ")
    source_text = {item.get("region_id"): item for item in scene.get("annotations", {}).get("text_regions", [])}
    if len(source_text) != len(scene.get("annotations", {}).get("text_regions", [])) or None in source_text:
        raise ValueError("source text identity differs")
    panels = {item.get("panel_id"): item for item in scene.get("panels", [])}
    if len(panels) != len(scene.get("panels", [])) or None in panels:
        raise ValueError("source panel identity differs")
    results, seen = [], set()
    for panel in annotation.get("panels", []):
        panel_id = panel.get("panel_id")
        if panel_id not in panels:
            raise ValueError("annotation panel is absent from source")
        for item in panel.get("texts", []):
            if item.get("role") != "x_tick" or item.get("visible") is not True:
                continue
            text_id, region_id, text = item.get("text_id"), item.get("region_id"), item.get("text")
            if (not isinstance(text_id, str) or not text_id or text_id in seen or text_id != region_id or
                    not isinstance(text, str) or not text.isascii() or not text.isdecimal() or region_id not in source_text):
                raise ValueError("X-tick text identity or numeric label differs")
            source = source_text[region_id]
            position = item.get("position")
            if (source.get("role") != "x_tick" or source.get("visible") is not True or
                    source.get("panel_id") != panel_id or source.get("text") != text or
                    item.get("coordinate_space") != "original_pixels" or not isinstance(position, list) or
                    len(position) != 2 or position != source.get("box", [])[:2] or item.get("partial") is not False):
                raise ValueError("X-tick source binding differs")
            matches = [tick for tick in panels[panel_id].get("ticks", [])
                       if tick.get("axis") == "x" and tick.get("hidden") is False and
                       tick.get("label") == text and type(tick.get("value")) in (int, float) and
                       math.isfinite(tick["value"]) and tick["value"] == int(text)]
            if len(matches) != 1:
                raise ValueError("X-tick does not bind exactly one visible source tick")
            left, top, right, bottom = _integer_box(item.get("rendered_pixel_box"), image.width, image.height)
            seen.add(text_id)
            results.append({"text_id": text_id, "region_id": region_id, "panel_id": panel_id, "text": text,
                            "rendered_pixel_box": item["rendered_pixel_box"], "crop_xyxy": [left, top, right, bottom]})
    if not results:
        raise ValueError("source scene lacks visible numeric X-tick labels")
    return results


def _assert_isolation(rows: list[dict], split_families: dict[str, dict[str, set[str]]], split_fonts: dict[str, set[str]]) -> None:
    observed_splits = {row["split"] for row in rows}
    if observed_splits != {"train", "validation"}:
        raise ValueError("corpus split population differs")
    for axis in FAMILY_AXES:
        if split_families["train"][axis] & split_families["validation"][axis]:
            raise ValueError(f"cross-split {axis} family overlap")
    if split_fonts["train"] & split_fonts["validation"]:
        raise ValueError("cross-split font byte overlap")
    for field in ("sample_id", "text_id"):
        values = [row[field] for row in rows]
        if len(values) != len(set(values)):
            raise ValueError(f"duplicate {field}")
    for field in ("source_image_sha256", "crop_sha256"):
        train = {row[field] for row in rows if row["split"] == "train"}
        validation = {row[field] for row in rows if row["split"] == "validation"}
        if train & validation:
            raise ValueError(f"cross-split {field} overlap")


def materialize(output: Path) -> dict:
    preflight = _preflight_binding()
    _require_public_train_validation_only()
    output = Path(output).resolve()
    artifacts_root = (ROOT / "artifacts").resolve()
    if not output.is_relative_to(artifacts_root) or output.exists():
        raise ValueError("output must be a new project-local artifacts directory")
    output.mkdir(parents=True, exist_ok=False)
    (output / "crops/train").mkdir(parents=True)
    (output / "crops/validation").mkdir(parents=True)
    rows: list[dict] = []
    split_families = {split: {axis: set() for axis in FAMILY_AXES} for split in ("train", "validation")}
    split_fonts = {split: set() for split in ("train", "validation")}
    fonts: dict[str, dict] = {}
    source_cases: list[dict] = []
    label_exclusions: Counter[str] = Counter()
    scene_exclusions: Counter[str] = Counter()
    with TemporaryDirectory(dir=output, prefix=".source-stage-") as temporary:
        stage = Path(temporary)
        for seed in SEEDS:
            generated = stage / str(seed)
            generate_dataset(PRESET, seed, generated)
            manifest = _json((generated / "seed-manifest.json").read_bytes())
            if manifest.get("preset") != PRESET or manifest.get("dataset_seed") != seed or manifest.get("sanity_passed") is not True:
                raise ValueError("generated source manifest differs")
            cases = manifest.get("cases")
            if not isinstance(cases, list) or len(cases) != 10 or any(case.get("split") == "test" for case in cases):
                raise ValueError("generator emitted an unexpected test case")
            for case in cases:
                split, scene_id = case.get("split"), case.get("scene_id")
                if split not in split_families or not isinstance(scene_id, str):
                    raise ValueError("source case split differs")
                families = case.get("families")
                if not isinstance(families, dict) or set(families) != set(FAMILY_AXES):
                    raise ValueError("source case family inventory differs")
                for axis in FAMILY_AXES:
                    key = families[axis]
                    if not isinstance(key, str) or key == "hand_drawn":
                        raise ValueError("reserved family appeared in public corpus")
                    split_families[split][axis].add(key)
                files = case.get("files", {})
                image_path = generated / "images" / f"{scene_id}.png"
                scene_path = generated / "scenes" / f"{scene_id}.json"
                annotation_path = generated / "annotations" / f"{scene_id}.json"
                image_raw, scene_raw, annotation_raw = image_path.read_bytes(), scene_path.read_bytes(), annotation_path.read_bytes()
                if (_digest(image_raw) != files.get(f"images/{scene_id}.png") or _digest(scene_raw) != files.get(f"scenes/{scene_id}.json") or
                        _digest(annotation_raw) != files.get(f"annotations/{scene_id}.json")):
                    raise ValueError("source artifact hash differs")
                with Image.open(image_path) as source_image:
                    source_image.load()
                    image = source_image.copy()
                scene, annotation = _json(scene_raw), _json(annotation_raw)
                font_sha = _font_sha(annotation)
                font = annotation["font"]
                split_fonts[split].add(font_sha)
                fonts.setdefault(font_sha, {key: font.get(key) for key in ("requested", "family", "style", "size_px", "source", "sha256", "bundled")})
                source_hash = _digest(image_raw)
                records = _x_tick_records(scene, annotation, image)
                retained = []
                for record in records:
                    glyph_mask_sha256, exclusion = _glyph_visibility(scene, annotation, image, record)
                    if exclusion is not None:
                        label_exclusions[exclusion] += 1
                        continue
                    retained.append({**record, "glyph_mask_sha256": glyph_mask_sha256})
                source_cases.append({"seed": seed, "scene_id": scene_id, "split": split, "families": families,
                                     "source_image_sha256": source_hash, "scene_sha256": _digest(scene_raw),
                                     "annotation_sha256": _digest(annotation_raw), "font_sha256": font_sha,
                                     "x_tick_count": len(records), "readable_x_tick_count": len(retained),
                                     "included": bool(retained),
                                     "exclusion_reason": None if retained else "source_scene_no_readable_x_tick_glyphs"})
                if not retained:
                    scene_exclusions["source_scene_no_readable_x_tick_glyphs"] += 1
                    continue
                for record in retained:
                    sample_id = f"seed-{seed}-{record['text_id']}"
                    crop_raw = png_bytes(image.crop(tuple(record["crop_xyxy"])).convert("RGB"))
                    relative = f"crops/{split}/{len(rows):06d}.png"
                    (output / relative).write_bytes(crop_raw)
                    rows.append({"sample_id": sample_id, "split": split, "seed": seed, "scene_id": scene_id,
                                 "families": families, "source_image_sha256": source_hash,
                                 "source_scene_sha256": _digest(scene_raw), "source_annotation_sha256": _digest(annotation_raw),
                                 "font_sha256": font_sha,
                                 "text_id": record["text_id"], "region_id": record["region_id"], "panel_id": record["panel_id"],
                                 "text": record["text"], "crop_xyxy": record["crop_xyxy"],
                                 "glyph_mask_sha256": record["glyph_mask_sha256"], "crop": relative,
                                 "crop_sha256": _digest(crop_raw)})
    _assert_isolation(rows, split_families, split_fonts)
    inventory = {row["crop"]: row["crop_sha256"] for row in rows}
    if len(inventory) != len(rows):
        raise ValueError("duplicate crop output path")
    report = {
        "schema": OUTPUT_SCHEMA, "preset": PRESET, "seeds": list(SEEDS), "full_gate_preflight": preflight,
        "source_generator_sha256": _source_hashes(),
        "pillow_version": version("Pillow"), "synthetic_only": True, "private_reads": 0, "sealed_reads": 0,
        "optimizer_steps": 0, "training_approved": False, "production_approved": False,
        "reserved_test_family_opened": False, "test_scene_count": 0,
        "historical_benchmark_independence": "unproven",
        "limitations": ["Historical benchmark independence is unproven.",
                        "This source-family-disjoint holdout is synthetic development evidence, not sealed acceptance evidence.",
                        "Resolved system font bytes and Pillow version are host-specific provenance."],
        "split_families": {split: {axis: sorted(values) for axis, values in axes.items()} for split, axes in split_families.items()},
        "font_provenance": dict(sorted(fonts.items())),
        "font_bytes_disjoint_across_splits": True,
        "font_license_review_status": "unreviewed_host_font_bytes_no_rights_asserted",
        "excluded_x_tick_labels": {"total": sum(label_exclusions.values()), "by_reason": dict(sorted(label_exclusions.items())),
                                    "scope": "aggregate only; excluded labels are not training samples"},
        "excluded_source_scenes": {"total": sum(scene_exclusions.values()), "by_reason": dict(sorted(scene_exclusions.items())),
                                    "scope": "aggregate only; source-scene exclusions do not increment label totals"},
        "source_cases": source_cases, "samples": rows,
        "artifact_sha256": inventory,
    }
    payload = canonical_json_bytes(report)
    (output / "manifest.json").write_bytes(payload)
    return {"manifest_sha256": _digest(payload), "train_samples": sum(row["split"] == "train" for row in rows),
            "validation_samples": sum(row["split"] == "validation" for row in rows), "source_case_count": len(source_cases)}


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    print(json.dumps(materialize(arguments.output), sort_keys=True))
