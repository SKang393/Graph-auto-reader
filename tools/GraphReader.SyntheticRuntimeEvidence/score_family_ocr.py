# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Score saved C# OCR regions against independently regenerated family truth.

This evaluator accepts only the annotation-free project-owned synthetic train
or validation exchange. It verifies every byte identity before renderer truth
is created, then applies the existing fixed IoU 0.5 maximum-cardinality matcher.
"""

from __future__ import annotations

import argparse
from collections import defaultdict
from hashlib import sha256
from io import BytesIO
import json
import math
from pathlib import Path
import sys
import time
from typing import Any, Mapping, Sequence
from uuid import UUID

from PIL import Image

REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
if str(REPOSITORY_ROOT) not in sys.path:
    sys.path.insert(0, str(REPOSITORY_ROOT))

from ml.markers.center.mask_preserving_v24.family_scenes import (
    _box,
    _family_identity,
    _records,
)
from ml.ocr.component_region_detector_v6.dataset import Box, Component
from ml.ocr.real_range_proposal_v34.pipeline import maximum_cardinality_matches
from ml.synthetic.dataset import PRESETS, _build_scenes, _scene_split
from ml.synthetic.renderer import render_scene


MANIFEST_SCHEMA = "graphreader.synthetic-runtime-raster-inputs.v1"
MANIFEST_SOURCE = "project-owned-synthetic-five-axis-family-v1"
REPORT_SCHEMA_V1 = "graphreader.synthetic-runtime-seed-evidence.v1"
REPORT_SCHEMA_V2 = "graphreader.synthetic-runtime-seed-evidence.v2"
# Retain the historical name for callers and saved v1 diagnostics.
REPORT_SCHEMA = REPORT_SCHEMA_V1
REPORT_SCOPE = "local-synthetic-seed-diagnostic"
OUTPUT_SCHEMA_V1 = "graphreader.synthetic-runtime-family-ocr-diagnostic.v1"
OUTPUT_SCHEMA_V2 = "graphreader.synthetic-runtime-family-ocr-diagnostic.v2"
OUTPUT_SCHEMA = OUTPUT_SCHEMA_V1
MATCH_IOU_MINIMUM = 0.5

MANIFEST_KEYS = {
    "schema",
    "source",
    "preset",
    "seed",
    "split",
    "contains_truth",
    "contains_precomputed_masks",
    "images",
}
IMAGE_KEYS = {"image", "image_sha256", "width", "height", "split", "family", "seed"}


class EvidenceError(ValueError):
    """The saved evidence cannot be safely joined to regenerated truth."""


def _sha256_bytes(payload: bytes) -> str:
    return sha256(payload).hexdigest()


def _sha256_file(path: Path) -> str:
    return _sha256_bytes(path.read_bytes())


def _load_object(path: Path, label: str) -> tuple[dict[str, Any], bytes]:
    payload = path.read_bytes()
    try:
        value = json.loads(payload)
    except json.JSONDecodeError as exception:
        raise EvidenceError(f"{label} is not valid JSON: {exception}") from exception
    if not isinstance(value, dict):
        raise EvidenceError(f"{label} must be a JSON object")
    return value, payload


def _require_exact_keys(record: Mapping[str, Any], expected: set[str], label: str) -> None:
    actual = set(record)
    if actual != expected:
        raise EvidenceError(
            f"{label} keys differ: missing={sorted(expected - actual)}, "
            f"unexpected={sorted(actual - expected)}"
        )


def _require_sha256(value: Any, label: str) -> str:
    normalized = str(value).casefold()
    if len(normalized) != 64 or any(character not in "0123456789abcdef" for character in normalized):
        raise EvidenceError(f"{label} must be a lowercase or uppercase SHA-256 value")
    return normalized


def _require_int(value: Any, label: str, *, minimum: int | None = None) -> int:
    if isinstance(value, bool) or not isinstance(value, int):
        raise EvidenceError(f"{label} must be an integer")
    if minimum is not None and value < minimum:
        raise EvidenceError(f"{label} must be at least {minimum}")
    return value


def _require_number(value: Any, label: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise EvidenceError(f"{label} must be numeric")
    result = float(value)
    if not math.isfinite(result):
        raise EvidenceError(f"{label} must be finite")
    return result


def _require_artifact_path(path: Path, repository_root: Path, label: str) -> Path:
    resolved = path.resolve()
    artifact_root = (repository_root / "artifacts").resolve()
    if resolved == artifact_root or artifact_root not in resolved.parents:
        raise EvidenceError(f"{label} must stay under the repository artifacts directory")
    return resolved


def _require_owned_file(
    directory: Path,
    name: Any,
    label: str,
    *,
    repository_root: Path | None = None,
) -> Path:
    if not isinstance(name, str) or Path(name).name != name:
        raise EvidenceError(f"{label} reference must be a local basename")
    root = REPOSITORY_ROOT if repository_root is None else repository_root
    expected_directory = _require_artifact_path(directory, root, f"{label} directory")
    path = _require_artifact_path(directory / name, root, label)
    if path.parent != expected_directory:
        raise EvidenceError(f"{label} must remain in its source/panel-owned directory")
    return path


def _validate_manifest(
    manifest: Mapping[str, Any],
    manifest_path: Path,
) -> tuple[str, int, list[dict[str, Any]]]:
    _require_exact_keys(manifest, MANIFEST_KEYS, "input manifest")
    split = str(manifest["split"])
    if (
        manifest["schema"] != MANIFEST_SCHEMA
        or manifest["source"] != MANIFEST_SOURCE
        or manifest["preset"] != "smoke"
        or split not in {"train", "validation"}
        or manifest["contains_truth"] is not False
        or manifest["contains_precomputed_masks"] is not False
    ):
        raise EvidenceError(
            "only annotation-free project-owned smoke train/validation inputs are accepted"
        )
    dataset_seed = _require_int(manifest["seed"], "input manifest seed")
    raw_images = manifest["images"]
    if not isinstance(raw_images, list) or not raw_images:
        raise EvidenceError("input manifest images must be a nonempty array")

    images: list[dict[str, Any]] = []
    names: set[str] = set()
    hashes: set[str] = set()
    seeds: set[int] = set()
    for index, raw in enumerate(raw_images):
        if not isinstance(raw, dict):
            raise EvidenceError(f"input manifest image {index} must be an object")
        _require_exact_keys(raw, IMAGE_KEYS, f"input manifest image {index}")
        name = str(raw["image"])
        path_name = Path(name)
        image_hash = _require_sha256(raw["image_sha256"], f"image {index} SHA-256")
        image_seed = _require_int(raw["seed"], f"image {index} seed")
        width = _require_int(raw["width"], f"image {index} width", minimum=1)
        height = _require_int(raw["height"], f"image {index} height", minimum=1)
        if path_name.name != name or path_name.suffix.casefold() != ".png":
            raise EvidenceError(f"image {index} must be a local PNG basename")
        if str(raw["split"]) != split:
            raise EvidenceError(f"image {index} split differs from the manifest split")
        if name.casefold() in names or image_hash in hashes or image_seed in seeds:
            raise EvidenceError("duplicate image name, hash, or seed in input manifest")
        names.add(name.casefold())
        hashes.add(image_hash)
        seeds.add(image_seed)
        image_path = manifest_path.parent / name
        if not image_path.is_file() or _sha256_file(image_path) != image_hash:
            raise EvidenceError(f"input image bytes do not match the manifest: {name}")
        images.append(
            {
                "image": name,
                "image_sha256": image_hash,
                "width": width,
                "height": height,
                "split": split,
                "family": str(raw["family"]),
                "seed": image_seed,
            }
        )
    return split, dataset_seed, images


def _validate_report(
    report: Mapping[str, Any],
    manifest_sha256: str,
    images: Sequence[Mapping[str, Any]],
    *,
    report_path: Path | None = None,
    manifest_path: Path | None = None,
) -> dict[str, Mapping[str, Any]]:
    schema = report.get("schema")
    if schema not in {REPORT_SCHEMA_V1, REPORT_SCHEMA_V2} or report.get("scope") != REPORT_SCOPE:
        raise EvidenceError("runtime report has a foreign schema or scope")
    if report.get("production_approved") is not False or report.get("training_input_ready") is not False:
        raise EvidenceError("runtime report must remain an unapproved local diagnostic")
    if _require_sha256(report.get("input_manifest_sha256"), "report input manifest SHA-256") != manifest_sha256:
        raise EvidenceError("runtime report does not bind the supplied input manifest bytes")

    if schema == REPORT_SCHEMA_V2:
        if report_path is None or manifest_path is None:
            raise EvidenceError("v2 runtime report validation requires report and manifest paths")
        return _validate_report_v2(report, images, report_path, manifest_path)

    raw_cases = report.get("cases")
    if not isinstance(raw_cases, list):
        raise EvidenceError("runtime report cases must be an array")
    case_map: dict[str, Mapping[str, Any]] = {}
    for index, raw in enumerate(raw_cases):
        if not isinstance(raw, dict):
            raise EvidenceError(f"runtime report case {index} must be an object")
        image_hash = _require_sha256(raw.get("image_sha256"), f"runtime case {index} image SHA-256")
        if image_hash in case_map:
            raise EvidenceError("runtime report contains duplicate case identities")
        if raw.get("status") not in {"seed-completed", "failed"}:
            raise EvidenceError(f"runtime case {index} has an unknown status")
        case_map[image_hash] = raw

    expected_hashes = {str(image["image_sha256"]) for image in images}
    actual_hashes = set(case_map)
    if actual_hashes != expected_hashes:
        raise EvidenceError(
            "runtime report case identities differ from the manifest: "
            f"missing={sorted(expected_hashes - actual_hashes)}, "
            f"foreign={sorted(actual_hashes - expected_hashes)}"
        )
    completed = sum(case.get("status") == "seed-completed" for case in case_map.values())
    failed = len(case_map) - completed
    if (
        _require_int(report.get("count"), "runtime report count", minimum=0) != len(images)
        or _require_int(report.get("completed"), "runtime report completed", minimum=0) != completed
        or _require_int(report.get("failed"), "runtime report failed", minimum=0) != failed
    ):
        raise EvidenceError("runtime report counts do not match its case identities and statuses")
    return case_map


def _rect(record: Any, label: str, *, integer: bool) -> tuple[float, float, float, float]:
    if not isinstance(record, dict):
        raise EvidenceError(f"{label} must be an object")
    _require_exact_keys(record, {"x", "y", "width", "height"}, label)
    if integer:
        values = tuple(float(_require_int(record[key], f"{label} {key}")) for key in (
            "x", "y", "width", "height"))
    else:
        values = tuple(_require_number(record[key], f"{label} {key}") for key in (
            "x", "y", "width", "height"))
    if values[2] <= 0 or values[3] <= 0:
        raise EvidenceError(f"{label} must have positive dimensions")
    return values


def _matrix(record: Any, expected: tuple[float, ...], label: str) -> None:
    if not isinstance(record, list) or len(record) != 9:
        raise EvidenceError(f"{label} must contain exactly nine values")
    values = tuple(_require_number(value, f"{label} value") for value in record)
    if values != expected:
        raise EvidenceError(f"{label} does not encode the exact crop translation")


def _validate_panel_png(
    panel: Mapping[str, Any],
    panel_directory: Path,
    source_path: Path,
    crop: tuple[int, int, int, int],
) -> None:
    record = panel.get("panel_png")
    if not isinstance(record, dict):
        raise EvidenceError("runtime panel is missing its crop PNG envelope")
    _require_exact_keys(record, {"file", "sha256", "byte_count"}, "runtime panel PNG")
    path = _require_owned_file(panel_directory, record.get("file"), "runtime panel PNG")
    if not path.is_file():
        raise EvidenceError("runtime panel PNG is missing")
    payload = path.read_bytes()
    payload_hash = _sha256_bytes(payload)
    if (
        payload_hash != _require_sha256(record.get("sha256"), "runtime panel PNG SHA-256")
        or payload_hash != _require_sha256(panel.get("image_sha256"), "runtime panel image SHA-256")
        or len(payload) != _require_int(record.get("byte_count"), "runtime panel PNG byte count", minimum=1)
    ):
        raise EvidenceError("runtime panel PNG bytes do not match the report")
    x, y, width, height = crop
    try:
        with Image.open(source_path) as source_image, Image.open(BytesIO(payload)) as panel_image:
            if source_image.format != "PNG" or panel_image.format != "PNG":
                raise EvidenceError("source and panel evidence must be PNG images")
            source_rgb = source_image.convert("RGB")
            panel_rgb = panel_image.convert("RGB")
            if panel_rgb.size != (width, height):
                raise EvidenceError("decoded panel PNG dimensions differ from its crop")
            if panel_rgb.tobytes() != source_rgb.crop((x, y, x + width, y + height)).tobytes():
                raise EvidenceError("decoded panel pixels differ from the immutable source crop")
    except (OSError, ValueError) as exception:
        raise EvidenceError(f"runtime panel PNG cannot be decoded: {exception}") from exception


def _validate_report_v2(
    report: Mapping[str, Any],
    images: Sequence[Mapping[str, Any]],
    report_path: Path,
    manifest_path: Path,
) -> dict[str, Mapping[str, Any]]:
    raw_cases = report.get("cases")
    if not isinstance(raw_cases, list):
        raise EvidenceError("runtime report cases must be an array")
    expected = {str(image["image_sha256"]): image for image in images}
    case_map: dict[str, Mapping[str, Any]] = {}
    panel_ids: set[str] = set()
    panel_count = completed_panels = failed_panels = 0
    completed_sources = 0
    for case_index, case in enumerate(raw_cases):
        if not isinstance(case, dict):
            raise EvidenceError(f"runtime source case {case_index} must be an object")
        source_hash = _require_sha256(
            case.get("image_sha256"), f"runtime source case {case_index} image SHA-256")
        image = expected.get(source_hash)
        if image is None or source_hash in case_map:
            raise EvidenceError("runtime report contains a foreign or duplicate source identity")
        if (
            _require_int(case.get("width"), "runtime source width", minimum=1) != image["width"]
            or _require_int(case.get("height"), "runtime source height", minimum=1) != image["height"]
        ):
            raise EvidenceError("runtime source dimensions differ from the manifest")
        source_path = manifest_path.parent / str(image["image"])
        if not source_path.is_file() or _sha256_file(source_path) != source_hash:
            raise EvidenceError("runtime source bytes differ from the manifest identity")
        try:
            with Image.open(source_path) as source_image:
                if source_image.format != "PNG" or source_image.size != (image["width"], image["height"]):
                    raise EvidenceError("decoded source PNG dimensions differ from the manifest")
        except (OSError, ValueError) as exception:
            raise EvidenceError(f"source PNG cannot be decoded: {exception}") from exception

        panels = case.get("panels")
        if not isinstance(panels, list):
            raise EvidenceError("runtime source panels must be an array")
        crops: list[tuple[int, int, int, int]] = []
        source_completed_panels = 0
        for panel_index, panel in enumerate(panels):
            if not isinstance(panel, dict):
                raise EvidenceError(f"runtime panel {panel_index} must be an object")
            try:
                panel_uuid = UUID(str(panel.get("panel_id")))
            except (ValueError, AttributeError) as exception:
                raise EvidenceError("runtime panel ID must be a GUID") from exception
            panel_id = str(panel_uuid)
            if str(panel.get("panel_id", "")).casefold() != panel_id or panel_id in panel_ids:
                raise EvidenceError("runtime panel IDs must be unique canonical GUIDs")
            panel_ids.add(panel_id)
            if _require_sha256(panel.get("source_image_sha256"), "panel source SHA-256") != source_hash:
                raise EvidenceError("runtime panel source identity differs from its source case")
            if (
                _require_int(panel.get("source_width"), "panel source width", minimum=1) != image["width"]
                or _require_int(panel.get("source_height"), "panel source height", minimum=1) != image["height"]
            ):
                raise EvidenceError("runtime panel source dimensions differ from the manifest")
            crop_values = _rect(panel.get("crop"), "runtime panel crop", integer=True)
            x, y, width, height = (int(value) for value in crop_values)
            if x < 0 or y < 0 or x + width > image["width"] or y + height > image["height"]:
                raise EvidenceError("runtime panel crop is outside the immutable source")
            if (
                _require_int(panel.get("width"), "runtime panel width", minimum=1) != width
                or _require_int(panel.get("height"), "runtime panel height", minimum=1) != height
            ):
                raise EvidenceError("runtime panel dimensions differ from its crop")
            requested = _rect(panel.get("requested_crop"), "runtime requested crop", integer=False)
            if (
                requested[0] < x or requested[1] < y
                or requested[0] + requested[2] > x + width
                or requested[1] + requested[3] > y + height
            ):
                raise EvidenceError("runtime requested crop is not contained by the actual crop")
            _matrix(panel.get("source_to_panel_matrix"),
                    (1.0, 0.0, -float(x), 0.0, 1.0, -float(y), 0.0, 0.0, 1.0),
                    "source-to-panel matrix")
            _matrix(panel.get("panel_to_source_matrix"),
                    (1.0, 0.0, float(x), 0.0, 1.0, float(y), 0.0, 0.0, 1.0),
                    "panel-to-source matrix")
            for prior_x, prior_y, prior_width, prior_height in crops:
                if min(x + width, prior_x + prior_width) > max(x, prior_x) and \
                        min(y + height, prior_y + prior_height) > max(y, prior_y):
                    raise EvidenceError("runtime panel crops overlap within a source")
            crops.append((x, y, width, height))
            warnings = panel.get("import_warnings")
            if not isinstance(warnings, list) or any(not isinstance(value, str) for value in warnings):
                raise EvidenceError("runtime panel import warnings must be an array of strings")
            panel_directory = report_path.parent / source_hash / panel_id
            _validate_panel_png(panel, panel_directory, source_path, (x, y, width, height))
            status = panel.get("status")
            if status == "seed-completed":
                source_completed_panels += 1
                completed_panels += 1
            elif status == "failed":
                failed_panels += 1
            else:
                raise EvidenceError("runtime panel has an unknown status")
            panel_count += 1

        source_success = bool(panels) and source_completed_panels == len(panels)
        expected_status = "panels-completed" if source_success else "failed"
        if case.get("status") != expected_status:
            raise EvidenceError("runtime source status is inconsistent with its panel statuses")
        if source_success:
            completed_sources += 1
        case_map[source_hash] = case

    actual_hashes = set(case_map)
    expected_hashes = set(expected)
    if actual_hashes != expected_hashes:
        raise EvidenceError(
            "runtime report source identities differ from the manifest: "
            f"missing={sorted(expected_hashes - actual_hashes)}, "
            f"foreign={sorted(actual_hashes - expected_hashes)}"
        )
    expected_counts = {
        "count": len(images),
        "completed": completed_sources,
        "failed": len(images) - completed_sources,
        "panel_count": panel_count,
        "completed_panels": completed_panels,
        "failed_panels": failed_panels,
    }
    for field, value in expected_counts.items():
        if _require_int(report.get(field), f"runtime report {field}", minimum=0) != value:
            raise EvidenceError("runtime report source or panel counts are inconsistent")
    return case_map


def _regenerate(
    split: str,
    dataset_seed: int,
    images: Sequence[Mapping[str, Any]],
) -> dict[str, tuple[Mapping[str, Any], Mapping[str, Any]]]:
    scenes = _build_scenes(
        PRESETS["smoke"], dataset_seed, require_complete_style_catalog=True
    )
    selected = [scene for scene in scenes if _scene_split(scene) == split]
    scenes_by_seed = {int(scene["seed"]): scene for scene in selected}
    expected_seeds = {int(image["seed"]) for image in images}
    if len(scenes_by_seed) != len(selected) or set(scenes_by_seed) != expected_seeds:
        raise EvidenceError("regenerated scene identities differ from the input manifest")

    regenerated: dict[str, tuple[Mapping[str, Any], Mapping[str, Any]]] = {}
    for image_record in images:
        scene = scenes_by_seed[int(image_record["seed"])]
        if _family_identity(scene) != image_record["family"]:
            raise EvidenceError(f"regenerated family differs for seed {image_record['seed']}")
        image, annotation, _ = render_scene(scene)
        rgb = image.convert("RGB")
        payload = BytesIO()
        rgb.save(payload, format="PNG")
        regenerated_hash = _sha256_bytes(payload.getvalue())
        expected_hash = str(image_record["image_sha256"])
        expected_name = f"{split}-{int(scene['seed'])}-{regenerated_hash[:12]}.png"
        if (
            regenerated_hash != expected_hash
            or expected_name != image_record["image"]
            or rgb.size != (image_record["width"], image_record["height"])
        ):
            raise EvidenceError(f"regenerated PNG identity differs for seed {scene['seed']}")
        regenerated[expected_hash] = (scene, annotation)
    return regenerated


def _truth_regions(annotation: Mapping[str, Any]) -> tuple[tuple[Box, ...], tuple[str, ...]]:
    boxes: list[Box] = []
    roles: list[str] = []
    for record in _records(annotation, "texts"):
        rendered_box = record.get("rendered_pixel_box")
        if (
            record.get("visible", True) is False
            or not str(record.get("text", "")).strip()
            or not isinstance(rendered_box, Sequence)
            or isinstance(rendered_box, (str, bytes))
        ):
            continue
        bounds = _box(record)
        if bounds is None:
            continue
        left, top, right, bottom = bounds
        if not all(math.isfinite(value) for value in bounds) or right <= left or bottom <= top:
            continue
        boxes.append(Box(left, top, right, bottom))
        roles.append(str(record.get("role", "other")))
    return tuple(boxes), tuple(roles)


def _prediction_regions(
    case: Mapping[str, Any],
    image: Mapping[str, Any],
    *,
    offset_x: int = 0,
    offset_y: int = 0,
    require_bounded: bool = False,
) -> tuple[Component, ...]:
    if case["status"] == "failed":
        return ()
    if case.get("width") != image["width"] or case.get("height") != image["height"]:
        raise EvidenceError("completed runtime case dimensions differ from the manifest")
    ocr = case.get("ocr")
    if not isinstance(ocr, dict):
        raise EvidenceError("completed runtime case is missing OCR output")
    if (
        ocr.get("contract_version") != 1
        or ocr.get("stage") != "ocr"
        or ocr.get("coordinate_space") != "original_pixels"
        or ocr.get("succeeded") is not True
        or ocr.get("failure") is not None
        or _require_sha256(ocr.get("input_sha256"), "OCR input SHA-256") != image["image_sha256"]
    ):
        raise EvidenceError("completed runtime OCR output has an invalid envelope or image identity")
    raw_regions = ocr.get("regions")
    if not isinstance(raw_regions, list):
        raise EvidenceError("completed runtime OCR regions must be an array")
    identifiers: set[str] = set()
    predictions: list[Component] = []
    for index, region in enumerate(raw_regions):
        if not isinstance(region, dict):
            raise EvidenceError(f"OCR region {index} must be an object")
        identifier = str(region.get("region_id", ""))
        if not identifier or identifier in identifiers:
            raise EvidenceError("OCR region identities must be present and unique within a case")
        identifiers.add(identifier)
        if region.get("coordinate_space") != "original_pixels":
            raise EvidenceError("OCR region is not in original pixel coordinates")
        polygon = region.get("polygon")
        bounds = polygon.get("bounds") if isinstance(polygon, dict) else None
        if not isinstance(bounds, dict):
            raise EvidenceError("OCR region is missing polygon bounds")
        left = float(bounds.get("left"))
        top = float(bounds.get("top"))
        right = float(bounds.get("right"))
        bottom = float(bounds.get("bottom"))
        coordinates = (left, top, right, bottom)
        if not all(math.isfinite(value) for value in coordinates) or right <= left or bottom <= top:
            raise EvidenceError("OCR region has nonpositive or nonfinite bounds")
        if require_bounded and (
            left < 0 or top < 0 or right > image["width"] or bottom > image["height"]
        ):
            raise EvidenceError("panel-local OCR region lies outside the panel crop")
        # Component stores inclusive right/bottom and exposes the half-open Box
        # consumed by the established matcher. Subtracting one preserves the
        # C# floating-point bounds exactly through Component.box. V2 applies
        # only the validated integer crop translation; graph semantics and box
        # extents are unchanged.
        predictions.append(
            Component(
                left + offset_x,
                top + offset_y,
                right + offset_x - 1.0,
                bottom + offset_y - 1.0,
                (right - left) * (bottom - top),
                1,
            )
        )
    return tuple(predictions)


def _source_prediction_regions(
    case: Mapping[str, Any],
    image: Mapping[str, Any],
    report_schema: str,
) -> tuple[Component, ...]:
    if report_schema == REPORT_SCHEMA_V1:
        return _prediction_regions(case, image)
    predictions: list[Component] = []
    for panel in case["panels"]:
        crop = panel["crop"]
        panel_image = {
            "width": panel["width"],
            "height": panel["height"],
            "image_sha256": panel["image_sha256"],
        }
        predictions.extend(_prediction_regions(
            panel,
            panel_image,
            offset_x=crop["x"],
            offset_y=crop["y"],
            require_bounded=True,
        ))
    return tuple(predictions)


def _truth_crop_coverage(
    truths: Sequence[Box],
    case: Mapping[str, Any],
    report_schema: str,
    *,
    completed_only: bool,
) -> int:
    if report_schema == REPORT_SCHEMA_V1:
        return len(truths) if case.get("status") == "seed-completed" else 0
    crops = [panel["crop"] for panel in case["panels"]
             if not completed_only or panel.get("status") == "seed-completed"]
    return sum(any(
        truth.left >= crop["x"] and truth.top >= crop["y"]
        and truth.right <= crop["x"] + crop["width"]
        and truth.bottom <= crop["y"] + crop["height"]
        for crop in crops
    ) for truth in truths)


def _source_binding(repository_root: Path) -> list[dict[str, str]]:
    paths = (
        Path(__file__).resolve(),
        repository_root / "ml/synthetic/dataset.py",
        repository_root / "ml/synthetic/renderer.py",
        repository_root / "ml/markers/center/mask_preserving_v24/family_scenes.py",
        repository_root / "ml/ocr/component_region_detector_v6/dataset.py",
        repository_root / "ml/ocr/real_range_proposal_v34/pipeline.py",
    )
    return [
        {
            "path": path.relative_to(repository_root).as_posix(),
            "sha256": _sha256_file(path),
        }
        for path in paths
    ]


def score(manifest_path: Path, report_path: Path, output_path: Path) -> dict[str, Any]:
    started = time.perf_counter()
    repository_root = REPOSITORY_ROOT
    manifest_path = _require_artifact_path(manifest_path, repository_root, "input manifest")
    report_path = _require_artifact_path(report_path, repository_root, "runtime report")
    output_path = _require_artifact_path(output_path, repository_root, "output")
    if output_path.exists():
        raise EvidenceError("refusing to replace an existing diagnostic output")

    manifest, manifest_bytes = _load_object(manifest_path, "input manifest")
    report, report_bytes = _load_object(report_path, "runtime report")
    split, dataset_seed, images = _validate_manifest(manifest, manifest_path)
    manifest_hash = _sha256_bytes(manifest_bytes)
    cases = _validate_report(
        report,
        manifest_hash,
        images,
        report_path=report_path,
        manifest_path=manifest_path,
    )
    report_schema = str(report["schema"])

    # Truth is regenerated only after the annotation-free runtime exchange and
    # saved report have passed all source, split, case, and byte-identity gates.
    regenerated = _regenerate(split, dataset_seed, images)

    total_truth = total_predictions = total_matches = 0
    failed_cases = 0
    failed_panels = 0
    total_panels = 0
    truth_outside_all_crops = 0
    truth_outside_completed_panels = 0
    role_counts: defaultdict[str, list[int]] = defaultdict(lambda: [0, 0])
    for image in images:
        image_hash = str(image["image_sha256"])
        case = cases[image_hash]
        if case["status"] == "failed":
            failed_cases += 1
        _, annotation = regenerated[image_hash]
        truths, roles = _truth_regions(annotation)
        predictions = _source_prediction_regions(case, image, report_schema)
        if report_schema == REPORT_SCHEMA_V2:
            panels = case["panels"]
            total_panels += len(panels)
            failed_panels += sum(panel["status"] == "failed" for panel in panels)
            truth_outside_all_crops += len(truths) - _truth_crop_coverage(
                truths, case, report_schema, completed_only=False)
            truth_outside_completed_panels += len(truths) - _truth_crop_coverage(
                truths, case, report_schema, completed_only=True)
        matched = maximum_cardinality_matches(predictions, truths)
        total_truth += len(truths)
        total_predictions += len(predictions)
        total_matches += matched
        for role in sorted(set(roles)):
            role_truths = tuple(box for box, current in zip(truths, roles) if current == role)
            role_matched = maximum_cardinality_matches(predictions, role_truths)
            role_counts[role][0] += len(role_truths)
            role_counts[role][1] += role_matched

    false_positives = total_predictions - total_matches
    false_negatives = total_truth - total_matches
    precision = total_matches / max(1, total_predictions)
    recall = total_matches / max(1, total_truth)

    bars_path = repository_root / "ml/policy/acceptance-bars.json"
    bars = json.loads(bars_path.read_text(encoding="utf-8"))["tier1_reviewable_error"]
    precision_bar = float(bars["text_region_detection_precision_minimum"])
    recall_bar = float(bars["text_region_detection_recall_minimum"])
    clears_reference_bar = precision >= precision_bar and recall >= recall_bar
    status = (
        "dev_diagnostic_clears_reference_bar_not_approval"
        if clears_reference_bar
        else "failed_dev_diagnostic"
    )
    failures: list[str] = []
    if precision < precision_bar:
        failures.append(
            f"detection precision {precision:.6f} is below the shared {precision_bar:.2f} reference bar"
        )
    if recall < recall_bar:
        failures.append(
            f"detection recall {recall:.6f} is below the shared {recall_bar:.2f} reference bar"
        )
    if failed_cases:
        if report_schema == REPORT_SCHEMA_V1:
            failures.append(f"{failed_cases} runtime case(s) failed and were scored as zero predictions")
        else:
            failures.append(
                f"{failed_cases} source image(s) failed full panel completion; successful sibling panels were retained"
            )
    if failed_panels:
        failures.append(f"{failed_panels} failed panel(s) contributed zero predictions")
    if truth_outside_all_crops:
        failures.append(
            f"{truth_outside_all_crops} full-source truth region(s) were outside every emitted panel crop"
        )

    case_identity_material = "\n".join(sorted(cases)).encode("ascii")
    result: dict[str, Any] = {
        "schema": OUTPUT_SCHEMA_V2 if report_schema == REPORT_SCHEMA_V2 else OUTPUT_SCHEMA_V1,
        "status": status,
        "scope": "local-synthetic-train-dev-diagnostic",
        "split": split,
        "synthetic_only": True,
        "private_data": False,
        "sealed_data": False,
        "sealed_runs": 0,
        "optimizer_steps": 0,
        "production_approval": False,
        "release_eligible": False,
        "truth_isolation": {
            "runtime_input_contains_truth": False,
            "runtime_input_contains_precomputed_masks": False,
            "truth_created_only_in_python_evaluator_after_provenance_validation": True,
        },
        "input_manifest": {
            "path": manifest_path.relative_to(repository_root).as_posix(),
            "sha256": manifest_hash,
            "source": MANIFEST_SOURCE,
            "preset": "smoke",
            "dataset_seed": dataset_seed,
            "case_count": len(images),
            "case_identity_set_sha256": _sha256_bytes(case_identity_material),
            "all_disk_and_regenerated_png_hashes_verified": True,
        },
        "runtime_report": {
            "path": report_path.relative_to(repository_root).as_posix(),
            "sha256": _sha256_bytes(report_bytes),
            "input_manifest_sha256_verified": True,
            "case_image_sha256_verified": True,
            "reported_completed": report["completed"],
            "reported_failed": report["failed"],
            **(
                {"failed_cases_scored_as_zero_predictions": failed_cases}
                if report_schema == REPORT_SCHEMA_V1 else
                {
                    "schema": report_schema,
                    "reported_panel_count": report["panel_count"],
                    "reported_completed_panels": report["completed_panels"],
                    "reported_failed_panels": report["failed_panels"],
                    "failed_sources_with_partial_or_zero_panel_predictions": failed_cases,
                    "failed_panels_scored_as_zero_predictions": failed_panels,
                    "source_and_panel_png_pixels_verified": True,
                }
            ),
        },
        "evaluator": {
            "matching": "existing maximum-cardinality one-to-one matching",
            "intersection_over_union_minimum": MATCH_IOU_MINIMUM,
            "box_convention": "half-open Box via inclusive Component right/bottom plus one",
            "sources": _source_binding(repository_root),
        },
        "reference_acceptance_bar": {
            "path": bars_path.relative_to(repository_root).as_posix(),
            "sha256": _sha256_file(bars_path),
            "precision_minimum": precision_bar,
            "recall_minimum": recall_bar,
            "clears_reference_bar": clears_reference_bar,
            "use": "descriptive comparison only; this diagnostic cannot approve a model",
        },
        "detection_metrics": {
            "case_count": len(images),
            "truth_region_count": total_truth,
            "predicted_region_count": total_predictions,
            "true_positives": total_matches,
            "false_positives": false_positives,
            "false_negatives": false_negatives,
            "precision": precision,
            "recall": recall,
            **(
                {
                    "panel_count": total_panels,
                    "truth_regions_outside_all_emitted_panel_crops": truth_outside_all_crops,
                    "truth_regions_not_fully_covered_by_completed_panels": truth_outside_completed_panels,
                }
                if report_schema == REPORT_SCHEMA_V2 else {}
            ),
            "by_truth_role": {
                role: {
                    "truth_region_count": counts[0],
                    "matched_truth_count": counts[1],
                    "false_negative_count": counts[0] - counts[1],
                    "recall": counts[1] / max(1, counts[0]),
                }
                for role, counts in sorted(role_counts.items())
            },
        },
        "recognition_metrics": {
            "scored": False,
            "reason": "recognition is not claimed without persisted one-to-one matched pairs",
        },
        "failure_mode": failures or ["none observed against the descriptive detection reference bar"],
        "limitations": (
            [
                "Synthetic runtime images are currently wrapped as one panel by the current axis path even when an image contains multiple rendered panels.",
                "This scores OCR region detection only and is not an end-to-end workflow, recognition, role, calibration, export, private-data, or production-approval result.",
                "The reference bar was read without selecting or changing a model, threshold, architecture, or weight.",
            ]
            if report_schema == REPORT_SCHEMA_V1 else
            [
                "V2 evaluates imported panel crops, but full-source truth remains the denominator, including content omitted by panelization.",
                "A failed panel without complete crop provenance and exact crop PNG bytes is rejected as ungradable evidence.",
                "This scores OCR region detection only and is not an end-to-end workflow, recognition, role, calibration, export, private-data, or production-approval result.",
                "The reference bar was read without selecting or changing a model, threshold, architecture, or weight.",
            ]
        ),
        "elapsed_milliseconds": round((time.perf_counter() - started) * 1000.0, 3),
    }
    output_path.parent.mkdir(parents=True, exist_ok=True)
    with output_path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(result, stream, indent=2, sort_keys=True)
        stream.write("\n")
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input-manifest", type=Path, required=True)
    parser.add_argument("--runtime-report", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    try:
        result = score(arguments.input_manifest, arguments.runtime_report, arguments.output)
    except (EvidenceError, OSError, KeyError, TypeError, json.JSONDecodeError) as exception:
        print(json.dumps({"status": "rejected", "error": str(exception)}, sort_keys=True), file=sys.stderr)
        return 1
    print(
        json.dumps(
            {
                "status": result["status"],
                "precision": result["detection_metrics"]["precision"],
                "recall": result["detection_metrics"]["recall"],
                "production_approval": False,
                "output": str(arguments.output),
            },
            sort_keys=True,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
