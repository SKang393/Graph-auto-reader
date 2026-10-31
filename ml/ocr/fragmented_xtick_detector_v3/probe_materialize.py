# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Generate a fresh, non-formal procedural probe for the frozen P4 checkpoint."""

from __future__ import annotations

from dataclasses import dataclass
from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path
import random
import stat
from typing import Any, Mapping

from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parent
REPO_ROOT = ROOT.parents[2]
ARTIFACT_ROOT = REPO_ROOT / "artifacts"
PROBE_ID = "fragmented-xtick-v3-fresh-procedural-probe-v1"
OUTPUT_RELATIVE = "artifacts/fragmented-xtick-detector-v3-fresh-probe-v1"
CANVAS = (256, 96)
BACKGROUND = 245
INK = 24
FONT_RELATIVES = (
    "src/GraphReader.App/Assets/Fonts/NotoSans-Medium.ttf",
    "src/GraphReader.App/Assets/Fonts/NotoSans-SemiBold.ttf",
)
FONT_SIZES = (14, 16, 20, 22)
LABELS = ("3", "4", "8", "15", "30", "60", "125", "7.5", "-5", "0.5", "200", "1.25")
TRAIN_LABELS = ("10", "12", "20", "25", "50", "100", "2.5", "-1")
POSITIVE_COUNT = 64
NEGATIVE_COUNT = 32


class ProbeMaterializationError(RuntimeError):
    """A fresh synthetic probe case or output violated its local contract."""


@dataclass(frozen=True)
class RenderedProbeCase:
    image: Image.Image
    glyph_mask: Image.Image
    annotation: dict[str, Any]


def _canonical_json(value: object) -> bytes:
    return json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(",", ":")).encode("utf-8")


def _sha256_bytes(value: bytes) -> str:
    return sha256(value).hexdigest()


def generator_sha256() -> str:
    return _sha256_bytes(Path(__file__).read_bytes())


def _font_path(relative: str) -> Path:
    path = REPO_ROOT / relative
    try:
        path.resolve(strict=True).relative_to(REPO_ROOT.resolve(strict=True))
    except (OSError, ValueError) as error:
        raise ProbeMaterializationError("probe font is missing or outside project root") from error
    if not path.is_file() or path.is_symlink() or getattr(path.stat(), "st_file_attributes", 0) & 0x400:
        raise ProbeMaterializationError("probe font is missing or traverses a reparse point")
    return path


def cases() -> tuple[dict[str, Any], ...]:
    result: list[dict[str, Any]] = []
    for index in range(POSITIVE_COUNT + NEGATIVE_COUNT):
        seed = 98765001 + index
        positive = index < POSITIVE_COUNT
        row: dict[str, Any] = {
            "case_id": f"{PROBE_ID}-{seed}",
            "seed": seed,
            "kind": "positive" if positive else "background_only",
            "split": "fresh_procedural_probe",
            "materialized": False,
        }
        if positive:
            row.update({
                "text": LABELS[index % len(LABELS)],
                "font_relative": FONT_RELATIVES[index % len(FONT_RELATIVES)],
                "font_size": FONT_SIZES[index % len(FONT_SIZES)],
                "origin": [8 + (index * 41) % 180, 8 + (index * 29) % 70],
                "gap_after": (index // 4) % max(1, len(LABELS[index % len(LABELS)]) - 1),
                "gap_pixels": (0, 1, 3, 4)[index % 4],
            })
        else:
            row.update({"text": None, "font_relative": None, "font_size": None, "origin": None, "gap_after": None, "gap_pixels": None})
        result.append(row)
    return tuple(result)


def _draw_fragmented_label(mask: Image.Image, case: Mapping[str, Any], font: ImageFont.FreeTypeFont) -> None:
    text = case["text"]
    x, y = case["origin"]
    draw = ImageDraw.Draw(mask)
    for index, character in enumerate(text):
        draw.text((x, y), character, fill=255, font=font)
        x += max(1, round(float(font.getlength(character))))
        if index == case["gap_after"]:
            x += int(case["gap_pixels"])


def _draw_graph_confusers(image: Image.Image, *, seed: int, origin: tuple[int, int], label_width: int) -> None:
    draw = ImageDraw.Draw(image)
    rng = random.Random(seed)
    baseline = max(5, min(89, origin[1] + rng.choice((-15, -10, 16, 21))))
    slope = rng.randint(-7, 7)
    second_slope = rng.randint(-6, 6)
    draw.line((6, baseline, 118, baseline + slope, 250, baseline + second_slope), fill=INK, width=rng.choice((1, 1, 2)))
    tick_x = max(5, min(250, origin[0] + rng.randint(-9, label_width + 9)))
    tick_y = max(3, min(90, baseline + slope // 2))
    draw.line((tick_x, tick_y, tick_x, min(95, tick_y + rng.randint(4, 11))), fill=INK, width=rng.choice((1, 2)))
    # A separate sloped trace passes near the label to vary line/glyph adjacency.
    trace_y = max(2, min(91, origin[1] + rng.choice((3, 8, 14))))
    trace_end = max(1, min(94, trace_y + rng.randint(-5, 5)))
    draw.line((max(1, origin[0] - 8), trace_y, min(254, origin[0] + label_width + 8), trace_end), fill=INK, width=1)
    marker_x = rng.randint(5, 249)
    marker_y = rng.randint(5, 89)
    radius = rng.choice((1, 2, 3))
    draw.ellipse((marker_x - radius, marker_y - radius, marker_x + radius, marker_y + radius), outline=INK, width=1)


def render_case(case: Mapping[str, Any]) -> RenderedProbeCase:
    if case.get("split") != "fresh_procedural_probe" or case.get("materialized") is not False:
        raise ProbeMaterializationError("only unmaterialized fresh-probe cases may be rendered")
    seed = case.get("seed")
    if type(seed) is not int:
        raise ProbeMaterializationError("probe seed is invalid")
    image = Image.new("L", CANVAS, BACKGROUND)
    glyph_mask = Image.new("L", CANVAS, 0)
    positive = case.get("kind") == "positive"
    if positive:
        text = case.get("text")
        relative = case.get("font_relative")
        size = case.get("font_size")
        origin = case.get("origin")
        if not isinstance(text, str) or text in TRAIN_LABELS or relative not in FONT_RELATIVES or size not in FONT_SIZES or not isinstance(origin, list) or len(origin) != 2:
            raise ProbeMaterializationError("positive probe specification is invalid or overlaps train labels")
        font = ImageFont.truetype(str(_font_path(relative)), size=int(size))
        _draw_fragmented_label(glyph_mask, case, font)
        bounds = glyph_mask.getbbox()
        if bounds is None or bounds[2] > CANVAS[0] or bounds[3] > CANVAS[1]:
            raise ProbeMaterializationError("rendered probe glyph bounds are empty or outside the canvas")
        _draw_graph_confusers(image, seed=seed, origin=(int(origin[0]), int(origin[1])), label_width=bounds[2] - bounds[0])
        image.paste(INK, mask=glyph_mask)
        target: dict[str, Any] | None = {
            "role": "XTick",
            "text": text,
            "bounding_box_xyxy": list(bounds),
            "visible_glyph_pixel_count": sum(bool(value) for value in glyph_mask.get_flattened_data()),
            "glyph_mask_sha256": _sha256_bytes(glyph_mask.tobytes()),
        }
    else:
        if case.get("kind") != "background_only":
            raise ProbeMaterializationError("unknown probe case kind")
        origin = (18 + seed % 170, 10 + seed % 72)
        _draw_graph_confusers(image, seed=seed, origin=origin, label_width=24)
        target = None
    annotation = {
        "schema": "graphreader.ocr-fragmented-xtick-v3-probe-annotation.v1",
        "case_id": case["case_id"],
        "split": "fresh_procedural_probe",
        "kind": case["kind"],
        "coordinate_space": "image_pixels_xyxy",
        "target": target,
        "font_relative": case.get("font_relative"),
        "font_size": case.get("font_size"),
        "font_sha256": _sha256_bytes(_font_path(case["font_relative"]).read_bytes()) if positive else None,
        "materialized": True,
    }
    return RenderedProbeCase(image=image, glyph_mask=glyph_mask, annotation=annotation)


def _png_bytes(image: Image.Image) -> bytes:
    output = BytesIO()
    image.save(output, format="PNG", compress_level=9, optimize=False)
    return output.getvalue()


def _is_reparse(path: Path) -> bool:
    return path.is_symlink() or bool(getattr(path.lstat(), "st_file_attributes", 0) & 0x400)


def _fresh_output() -> Path:
    output = REPO_ROOT / OUTPUT_RELATIVE
    try:
        output.relative_to(ARTIFACT_ROOT)
    except ValueError as error:
        raise ProbeMaterializationError("probe output escapes artifacts") from error
    cursor = ARTIFACT_ROOT
    if _is_reparse(cursor):
        raise ProbeMaterializationError("artifacts root is a reparse point")
    for part in output.relative_to(ARTIFACT_ROOT).parts:
        cursor = cursor / part
        if cursor.exists() and _is_reparse(cursor):
            raise ProbeMaterializationError("probe output traverses a reparse point")
    if output.exists():
        raise ProbeMaterializationError("probe output must be fresh")
    return output


def materialize() -> dict[str, Any]:
    output = _fresh_output()
    output.mkdir(parents=True, exist_ok=False)
    (output / "images").mkdir()
    (output / "annotations").mkdir()
    records = []
    positives = negatives = 0
    for case in cases():
        rendered = render_case(case)
        stem = case["case_id"]
        image_bytes = _png_bytes(rendered.image)
        annotation = dict(rendered.annotation)
        annotation["image_sha256"] = _sha256_bytes(image_bytes)
        annotation_bytes = _canonical_json(annotation)
        image_relative = f"images/{stem}.png"
        annotation_relative = f"annotations/{stem}.json"
        (output / image_relative).write_bytes(image_bytes)
        (output / annotation_relative).write_bytes(annotation_bytes)
        if annotation["target"] is None:
            negatives += 1
        else:
            positives += 1
        records.append({
            "case_id": stem,
            "kind": case["kind"],
            "image_path": image_relative,
            "image_sha256": _sha256_bytes(image_bytes),
            "annotation_path": annotation_relative,
            "annotation_sha256": _sha256_bytes(annotation_bytes),
        })
    if (positives, negatives) != (POSITIVE_COUNT, NEGATIVE_COUNT):
        raise ProbeMaterializationError("probe counts changed")
    manifest = {
        "schema": "graphreader.ocr-fragmented-xtick-v3-fresh-probe.v1",
        "probe_id": PROBE_ID,
        "generator_sha256": generator_sha256(),
        "case_count": len(records),
        "positive_count": positives,
        "background_only_count": negatives,
        "canvas": list(CANVAS),
        "font_paths": list(FONT_RELATIVES),
        "font_sha256": {relative: _sha256_bytes(_font_path(relative).read_bytes()) for relative in FONT_RELATIVES},
        "font_sizes": list(FONT_SIZES),
        "seed_start": 98765001,
        "labels_disjoint_from_train": True,
        "train_distribution_reference": {
            "train_labels": list(TRAIN_LABELS),
            "train_font": "NotoSans-Regular.ttf at 18px",
            "train_origin_formula": "x=24+(i*29)%162, y=34+(i*17)%28",
            "train_glyph_gap_pixels": 2,
        },
        "probe_distribution": {
            "fonts": [Path(relative).name for relative in FONT_RELATIVES],
            "font_sizes": list(FONT_SIZES),
            "placement_formula": "x=8+(i*41)%180, y=8+(i*29)%70",
            "glyph_gap_pixels": [0, 1, 3, 4],
            "confusers": "sloped axes, variable ticks, nearby trace segments, and hollow markers",
        },
        "distribution_overlap_and_limits": "Probe labels and exact font files/sizes differ from the 96 P4 training cases. It shares the Noto Sans family, 256x96 grayscale canvas, ink/background, one-label task, and broad numeric tick semantics. Placement ranges partially overlap training. This is a fresh synthetic stress probe, not held-out real data or a formal acceptance set.",
        "records": records,
        "formal_acceptance_set": False,
        "dev_sealed_private_inputs_read": False,
    }
    manifest_bytes = _canonical_json(manifest)
    (output / "manifest.json").write_bytes(manifest_bytes)
    return {"output_root": output.relative_to(REPO_ROOT).as_posix(), "manifest_sha256": _sha256_bytes(manifest_bytes), "case_count": len(records)}


if __name__ == "__main__":
    print(json.dumps(materialize(), indent=2, sort_keys=True))
