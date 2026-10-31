# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Render only fresh train X-tick cases into a fresh project-local artifact."""

from __future__ import annotations

import argparse
from dataclasses import dataclass
from hashlib import sha256
from io import BytesIO
import json
import os
from pathlib import Path
import stat
import sys
from typing import Any, Mapping

from PIL import Image, ImageDraw, ImageFont, __version__ as PILLOW_VERSION

if __package__:
    from .prepare import (
        FAMILY, PROTOCOL_PATH, REVISION, PreparationError, _canonical_json,
        _read_protocol, _sha256, build_preflight, case_specifications,
        registrations, source_bundle_sha256,
    )
else:
    sys.path.insert(0, str(Path(__file__).resolve().parents[3]))
    from ml.ocr.fragmented_xtick_detector_v1.prepare import (
        FAMILY, PROTOCOL_PATH, REVISION, PreparationError, _canonical_json,
        _read_protocol, _sha256, build_preflight, case_specifications,
        registrations, source_bundle_sha256,
    )


ROOT = Path(__file__).resolve().parent
REPO_ROOT = ROOT.parents[2]
ARTIFACTS_ROOT = REPO_ROOT / "artifacts"
CANVAS_SIZE = (256, 96)
BACKGROUND = 245
INK = 24


class MaterializationError(RuntimeError):
    """Train materialization is outside its source, split, or output contract."""


@dataclass(frozen=True)
class RenderedTrainCase:
    image: Image.Image
    annotation: dict[str, object]
    glyph_mask: Image.Image


def _is_reparse(path: Path) -> bool:
    try:
        attributes = getattr(path.lstat(), "st_file_attributes", 0)
    except OSError:
        return path.is_symlink()
    return path.is_symlink() or bool(attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400))


def _require_non_reparse_ancestor(path: Path, root: Path) -> None:
    try:
        relative = path.relative_to(root)
    except ValueError as error:
        raise MaterializationError("Output must stay under the project artifacts directory") from error
    cursor = root
    if _is_reparse(cursor):
        raise MaterializationError("Project artifacts root is a reparse point")
    for part in relative.parts:
        cursor = cursor / part
        if cursor.exists() and _is_reparse(cursor):
            raise MaterializationError("Output path traverses a reparse point")


def _font_path() -> Path:
    protocol = _read_protocol()
    materialization = protocol.get("train_materialization")
    if not isinstance(materialization, dict):
        raise MaterializationError("Train materialization protocol is missing")
    relative = materialization.get("font_path")
    expected_hash = materialization.get("font_sha256")
    if not isinstance(relative, str) or not isinstance(expected_hash, str):
        raise MaterializationError("Train materialization font binding is malformed")
    path = REPO_ROOT / relative
    if not path.is_file() or _is_reparse(path):
        raise MaterializationError("Train materialization font is unavailable or unsafe")
    if _sha256(path) != expected_hash:
        raise MaterializationError("Train materialization font hash changed")
    return path


def _draw_fragmented_label(mask: Image.Image, *, text: str, variant: str, font: ImageFont.FreeTypeFont) -> None:
    draw = ImageDraw.Draw(mask)
    x, y = 96, 68
    split_after = {
        "left-gap": {0},
        "middle-gap": {max(0, len(text) // 2 - 1)},
        "right-gap": {max(0, len(text) - 2)},
        "double-gap": set(range(max(0, len(text) - 1))),
    }.get(variant)
    if split_after is None:
        raise MaterializationError("Unknown fragmented X-tick variant")
    for index, character in enumerate(text):
        draw.text((x, y), character, fill=255, font=font)
        advance = round(float(font.getlength(character)))
        x += max(1, advance)
        if index in split_after:
            x += 2


def _visible_bounds(image: Image.Image) -> tuple[int, int, int, int]:
    bounds = image.getbbox()
    if bounds is None:
        raise MaterializationError("Rendered X-tick label has no visible glyphs")
    return tuple(int(value) for value in bounds)


def _annotation(case: Mapping[str, object], glyph_mask: Image.Image, image: Image.Image) -> dict[str, object]:
    positive = case.get("positive")
    if not isinstance(positive, dict):
        raise MaterializationError("Train case positive annotation is malformed")
    bounds = _visible_bounds(glyph_mask)
    visible_pixels = sum(1 for value in glyph_mask.get_flattened_data() if value)
    if visible_pixels <= 0:
        raise MaterializationError("Rendered X-tick label has zero visible pixels")
    crop = image.crop(bounds)
    if not any(value < BACKGROUND for value in crop.get_flattened_data()):
        raise MaterializationError("Target bounds do not contain visible glyph pixels")
    return {
        "schema": "graphreader.ocr-fragmented-xtick-train-annotation.v1",
        "case_id": case["case_id"],
        "case_identity_sha256": case["case_identity_sha256"],
        "split": "train",
        "coordinate_space": "image_pixels_xyxy",
        "target": {
            "role": positive["role"],
            "text": positive["text"],
            "numeric": True,
            "complete_label_required": True,
            "bounding_box_xyxy": list(bounds),
            "visible_glyph_pixel_count": visible_pixels,
            "glyph_mask_sha256": sha256(glyph_mask.tobytes()).hexdigest(),
        },
        "negative_confusers": case["negative_confusers"],
        "materialized": True,
    }


def render_train_case(case: Mapping[str, object]) -> RenderedTrainCase:
    """Render one registered fresh train case and derive its visible-glyph target."""

    if case.get("split") != "train" or case.get("materialized") is not False:
        raise MaterializationError("Only unmaterialized fresh train cases may be rendered")
    positive = case.get("positive")
    if not isinstance(positive, dict) or positive.get("role") != "XTick" or positive.get("numeric") is not True:
        raise MaterializationError("Train case is not a numeric X-tick label")
    text = positive.get("text")
    variant = positive.get("fragment_variant")
    if not isinstance(text, str) or len(text) < 2 or not isinstance(variant, str):
        raise MaterializationError("Train case does not contain a fragmentable numeric label")
    font = ImageFont.truetype(str(_font_path()), size=18)
    image = Image.new("L", CANVAS_SIZE, color=BACKGROUND)
    draw = ImageDraw.Draw(image)
    draw.line((28, 60, 228, 60), fill=INK, width=2)
    draw.line((128, 60, 128, 66), fill=INK, width=2)
    glyph_mask = Image.new("L", CANVAS_SIZE, color=0)
    _draw_fragmented_label(glyph_mask, text=text, variant=variant, font=font)
    bounds = _visible_bounds(glyph_mask)
    draw.ellipse((bounds[2] + 7, 74, bounds[2] + 10, 77), fill=INK)
    image.paste(INK, mask=glyph_mask)
    annotation = _annotation(case, glyph_mask, image)
    return RenderedTrainCase(image=image, annotation=annotation, glyph_mask=glyph_mask)


def _png_bytes(image: Image.Image) -> bytes:
    stream = BytesIO()
    image.save(stream, format="PNG", compress_level=9, optimize=False)
    return stream.getvalue()


def _safe_output_root(value: Path) -> Path:
    candidate = value if value.is_absolute() else REPO_ROOT / value
    candidate = candidate.absolute()
    artifacts = ARTIFACTS_ROOT.absolute()
    _require_non_reparse_ancestor(candidate.parent, artifacts)
    if candidate.exists() or candidate.is_symlink():
        raise MaterializationError("Train materialization requires a fresh output directory")
    return candidate


def materialize_train(output_root: Path) -> dict[str, object]:
    """Exclusively write the 96 fresh train images and their annotations."""

    preflight = build_preflight()
    candidate = _safe_output_root(output_root)
    train = next(item for item in registrations() if item.split == "train")
    cases = case_specifications(train)
    candidate.mkdir(parents=True, exist_ok=False)
    images = candidate / "images"
    annotations = candidate / "annotations"
    images.mkdir()
    annotations.mkdir()
    records: list[dict[str, object]] = []
    for case in cases:
        rendered = render_train_case(case)
        stem = str(case["case_id"])
        image_path = images / f"{stem}.png"
        annotation_path = annotations / f"{stem}.json"
        image_bytes = _png_bytes(rendered.image)
        payload = dict(rendered.annotation)
        payload["image_path"] = image_path.relative_to(candidate).as_posix()
        payload["image_sha256"] = sha256(image_bytes).hexdigest()
        payload["image_size"] = len(image_bytes)
        payload["annotation_path"] = annotation_path.relative_to(candidate).as_posix()
        annotation_bytes = _canonical_json(payload)
        with image_path.open("xb") as stream:
            stream.write(image_bytes)
        with annotation_path.open("xb") as stream:
            stream.write(annotation_bytes)
        records.append({
            "case_id": case["case_id"],
            "case_identity_sha256": case["case_identity_sha256"],
            "image_path": payload["image_path"],
            "image_sha256": payload["image_sha256"],
            "annotation_path": payload["annotation_path"],
            "annotation_sha256": sha256(annotation_bytes).hexdigest(),
        })
    manifest = {
        "schema": "graphreader.ocr-fragmented-xtick-train-materialization.v1",
        "revision": REVISION,
        "split": "train",
        "case_count": len(records),
        "source_bundle_sha256": source_bundle_sha256(),
        "protocol_sha256": _sha256(PROTOCOL_PATH),
        "font_path": _font_path().relative_to(REPO_ROOT).as_posix(),
        "font_sha256": _sha256(_font_path()),
        "pillow_version": PILLOW_VERSION,
        "preflight": preflight,
        "records": records,
        "synthetic_only": True,
        "development_or_sealed_cases_rendered": False,
        "weights_loaded": False,
        "optimizer_steps": 0,
        "inference_executed": False,
        "production_approval": False,
        "release_eligible": False,
    }
    manifest_bytes = _canonical_json(manifest)
    with (candidate / "manifest.json").open("xb") as stream:
        stream.write(manifest_bytes)
    return {
        "output_root": candidate.relative_to(REPO_ROOT).as_posix(),
        "manifest_sha256": sha256(manifest_bytes).hexdigest(),
        "case_count": len(records),
        "split": "train",
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True, help="fresh directory under E:\\Graph-auto-reader\\artifacts")
    arguments = parser.parse_args()
    print(json.dumps(materialize_train(arguments.output), indent=2, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
