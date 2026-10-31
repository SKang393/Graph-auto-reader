# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Fresh V2 train-only varied-position fragmented X-tick materializer."""

from __future__ import annotations

import argparse
from dataclasses import dataclass
from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path
import stat
from typing import Any, Mapping

from PIL import Image, ImageDraw, ImageFont, __version__ as PILLOW_VERSION


ROOT = Path(__file__).resolve().parent
REPO_ROOT = ROOT.parents[2]
PROTOCOL_PATH = ROOT / "PROTOCOL.json"
ARTIFACTS_ROOT = REPO_ROOT / "artifacts"
CANVAS = (256, 96)
BACKGROUND = 245
INK = 24
FAMILY = "fresh-fragmented-numeric-xtick-v2"
SOURCE_PATHS = ("__init__.py", "PROTOCOL.json", "materialize.py", "test_materialize.py")
LABELS = ("10", "12", "20", "25", "50", "100", "2.5", "-1")
VARIANTS = ("left-gap", "middle-gap", "right-gap", "double-gap")


class MaterializationError(RuntimeError):
    """The fresh V2 train materialization violates a bound safety contract."""


@dataclass(frozen=True)
class RenderedCase:
    image: Image.Image
    annotation: dict[str, object]


def _canonical_json(value: object) -> bytes:
    return json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(",", ":")).encode("utf-8")


def _sha256_bytes(value: bytes) -> str:
    return sha256(value).hexdigest()


def _sha256_file(path: Path) -> str:
    return _sha256_bytes(path.read_bytes())


def _read_protocol() -> dict[str, Any]:
    try:
        value = json.loads(PROTOCOL_PATH.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise MaterializationError("V2 protocol is unreadable") from error
    if not isinstance(value, dict):
        raise MaterializationError("V2 protocol must be an object")
    expected = {
        "schema": "graphreader.ocr-fragmented-xtick-v2-preparation.v1",
        "revision": "graph-text-fragmented-xtick-detector-v2",
        "task": "ocr-detection",
        "state": "train-only-materializer-preparation",
        "positive_count": 64,
        "negative_count": 32,
        "canvas": [256, 96],
        "train_materialization_authorized": True,
        "development_or_sealed_rendering_authorized": False,
        "optimizer_execution_authorized": False,
        "production_approval": False,
        "release_eligible": False,
    }
    for key, expected_value in expected.items():
        if value.get(key) != expected_value:
            raise MaterializationError(f"V2 protocol field changed: {key}")
    return value


def _is_reparse(path: Path) -> bool:
    try:
        attributes = getattr(path.lstat(), "st_file_attributes", 0)
    except OSError:
        return path.is_symlink()
    return path.is_symlink() or bool(attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400))


def _font(protocol: Mapping[str, Any]) -> ImageFont.FreeTypeFont:
    relative = protocol.get("font_path")
    expected_hash = protocol.get("font_sha256")
    if not isinstance(relative, str) or not isinstance(expected_hash, str):
        raise MaterializationError("V2 font binding is malformed")
    path = REPO_ROOT / relative
    if not path.is_file() or _is_reparse(path) or _sha256_file(path) != expected_hash:
        raise MaterializationError("V2 font binding is unavailable or changed")
    return ImageFont.truetype(str(path), size=18)


def source_bundle_sha256() -> str:
    digest = sha256()
    for relative in SOURCE_PATHS:
        path = ROOT / relative
        if not path.is_file():
            raise MaterializationError(f"V2 source is missing: {relative}")
        digest.update(relative.encode("utf-8"))
        digest.update(b"\0")
        digest.update(path.read_bytes())
        digest.update(b"\0")
    return digest.hexdigest()


def cases() -> tuple[dict[str, object], ...]:
    """Return the 64 varied positive and 32 background-only train specifications."""

    result: list[dict[str, object]] = []
    for index in range(96):
        positive = index < 64
        seed = 24014101 + index
        case_id = f"{FAMILY}-train-{seed}"
        row: dict[str, object] = {
            "case_id": case_id,
            "case_identity_sha256": _sha256_bytes(_canonical_json({"family": FAMILY, "seed": seed, "positive": positive})),
            "split": "train",
            "seed": seed,
            "kind": "positive" if positive else "background_negative",
            "materialized": False,
        }
        if positive:
            row["text"] = LABELS[index % len(LABELS)]
            row["variant"] = VARIANTS[index % len(VARIANTS)]
            row["label_origin"] = [24 + (index * 29) % 162, 34 + (index * 17) % 28]
        else:
            row["label_origin"] = None
        result.append(row)
    return tuple(result)


def _draw_label(mask: Image.Image, text: str, variant: str, origin: tuple[int, int], font: ImageFont.FreeTypeFont) -> None:
    gaps = {
        "left-gap": {0},
        "middle-gap": {max(0, len(text) // 2 - 1)},
        "right-gap": {max(0, len(text) - 2)},
        "double-gap": set(range(max(0, len(text) - 1))),
    }.get(variant)
    if gaps is None:
        raise MaterializationError("V2 label variant is invalid")
    draw = ImageDraw.Draw(mask)
    x, y = origin
    for index, character in enumerate(text):
        draw.text((x, y), character, fill=255, font=font)
        x += max(1, round(float(font.getlength(character))))
        if index in gaps:
            x += 2


def _draw_confusers(image: Image.Image, *, seed: int, anchor: tuple[int, int]) -> None:
    draw = ImageDraw.Draw(image)
    x, y = anchor
    baseline = max(8, y - 9)
    draw.line((10, baseline, 246, baseline), fill=INK, width=2)
    tick_x = min(242, max(14, x + 8))
    draw.line((tick_x, baseline, tick_x, baseline + 7), fill=INK, width=2)
    dot_x = min(248, max(4, x + 42 + seed % 17))
    dot_y = min(88, max(5, y + 12 + seed % 9))
    draw.ellipse((dot_x, dot_y, dot_x + 3, dot_y + 3), fill=INK)


def render_case(case: Mapping[str, object]) -> RenderedCase:
    if case.get("split") != "train" or case.get("materialized") is not False:
        raise MaterializationError("V2 only renders fresh train cases")
    protocol = _read_protocol()
    font = _font(protocol)
    image = Image.new("L", CANVAS, color=BACKGROUND)
    kind = case.get("kind")
    seed = case.get("seed")
    if type(seed) is not int:
        raise MaterializationError("V2 case seed is invalid")
    origin_value = case.get("label_origin")
    anchor = tuple(origin_value) if isinstance(origin_value, list) and len(origin_value) == 2 else (48 + seed % 140, 48 + seed % 24)
    _draw_confusers(image, seed=seed, anchor=(int(anchor[0]), int(anchor[1])))
    target: dict[str, object] | None = None
    if kind == "positive":
        text, variant = case.get("text"), case.get("variant")
        if not isinstance(text, str) or len(text) < 2 or not isinstance(variant, str) or not isinstance(origin_value, list):
            raise MaterializationError("V2 positive case is invalid")
        mask = Image.new("L", CANVAS, color=0)
        _draw_label(mask, text, variant, (int(origin_value[0]), int(origin_value[1])), font)
        bounds = mask.getbbox()
        if bounds is None:
            raise MaterializationError("V2 positive glyph has no visible pixels")
        image.paste(INK, mask=mask)
        target = {
            "role": "XTick",
            "text": text,
            "bounding_box_xyxy": list(bounds),
            "visible_glyph_pixel_count": sum(1 for value in mask.get_flattened_data() if value),
            "glyph_mask_sha256": _sha256_bytes(mask.tobytes()),
        }
    elif kind != "background_negative":
        raise MaterializationError("V2 case kind is invalid")
    annotation = {
        "schema": "graphreader.ocr-fragmented-xtick-v2-train-annotation.v1",
        "case_id": case["case_id"],
        "case_identity_sha256": case["case_identity_sha256"],
        "split": "train",
        "kind": kind,
        "coordinate_space": "image_pixels_xyxy",
        "target": target,
        "materialized": True,
    }
    return RenderedCase(image=image, annotation=annotation)


def _png_bytes(image: Image.Image) -> bytes:
    stream = BytesIO()
    image.save(stream, format="PNG", compress_level=9, optimize=False)
    return stream.getvalue()


def _safe_new_output(value: Path) -> Path:
    candidate = value if value.is_absolute() else REPO_ROOT / value
    candidate = candidate.absolute()
    artifacts = ARTIFACTS_ROOT.absolute()
    try:
        relative = candidate.relative_to(artifacts)
    except ValueError as error:
        raise MaterializationError("V2 output must stay under project artifacts") from error
    cursor = artifacts
    if _is_reparse(cursor):
        raise MaterializationError("V2 artifacts root is a reparse point")
    for part in relative.parts:
        cursor = cursor / part
        if cursor.exists() and _is_reparse(cursor):
            raise MaterializationError("V2 output traverses a reparse point")
    if candidate.exists() or candidate.is_symlink():
        raise MaterializationError("V2 output must be a fresh directory")
    return candidate


def materialize_train(output_root: Path) -> dict[str, object]:
    protocol = _read_protocol()
    output = _safe_new_output(output_root)
    train_cases = cases()
    output.mkdir(parents=True, exist_ok=False)
    (output / "images").mkdir()
    (output / "annotations").mkdir()
    records: list[dict[str, object]] = []
    centers: list[tuple[float, float]] = []
    positive_count = negative_count = 0
    for case in train_cases:
        rendered = render_case(case)
        stem = str(case["case_id"])
        image_path = output / "images" / f"{stem}.png"
        annotation_path = output / "annotations" / f"{stem}.json"
        image_bytes = _png_bytes(rendered.image)
        annotation = dict(rendered.annotation)
        annotation.update({
            "image_path": image_path.relative_to(output).as_posix(),
            "image_sha256": _sha256_bytes(image_bytes),
            "image_size": len(image_bytes),
            "annotation_path": annotation_path.relative_to(output).as_posix(),
        })
        annotation_bytes = _canonical_json(annotation)
        with image_path.open("xb") as stream:
            stream.write(image_bytes)
        with annotation_path.open("xb") as stream:
            stream.write(annotation_bytes)
        target = annotation["target"]
        if isinstance(target, dict):
            bounds = target["bounding_box_xyxy"]
            centers.append(((bounds[0] + bounds[2]) / 2.0, (bounds[1] + bounds[3]) / 2.0))
            positive_count += 1
        else:
            negative_count += 1
        records.append({
            "case_id": case["case_id"], "case_identity_sha256": case["case_identity_sha256"],
            "kind": case["kind"], "image_path": annotation["image_path"],
            "image_sha256": annotation["image_sha256"], "annotation_path": annotation["annotation_path"],
            "annotation_sha256": _sha256_bytes(annotation_bytes),
        })
    x_bins = {int(x // 24) for x, _ in centers}
    y_bins = {int(y // 12) for _, y in centers}
    if positive_count != 64 or negative_count != 32 or len(x_bins) < 8 or len(y_bins) < 4:
        raise MaterializationError("V2 localization position or negative balance contract failed")
    manifest = {
        "schema": "graphreader.ocr-fragmented-xtick-v2-train-materialization.v1",
        "revision": protocol["revision"], "split": "train", "case_count": len(records),
        "positive_count": positive_count, "background_negative_count": negative_count,
        "target_center_x_bin_count": len(x_bins), "target_center_y_bin_count": len(y_bins),
        "source_bundle_sha256": source_bundle_sha256(), "protocol_sha256": _sha256_file(PROTOCOL_PATH),
        "font_path": protocol["font_path"], "font_sha256": protocol["font_sha256"], "pillow_version": PILLOW_VERSION,
        "records": records, "synthetic_only": True, "dev_or_sealed_cases_rendered": False,
        "weights_loaded": False, "optimizer_steps": 0, "inference_executed": False,
        "production_approval": False, "release_eligible": False,
    }
    manifest_bytes = _canonical_json(manifest)
    with (output / "manifest.json").open("xb") as stream:
        stream.write(manifest_bytes)
    return {"output_root": output.relative_to(REPO_ROOT).as_posix(), "manifest_sha256": _sha256_bytes(manifest_bytes), "case_count": len(records)}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    print(json.dumps(materialize_train(arguments.output), indent=2, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
