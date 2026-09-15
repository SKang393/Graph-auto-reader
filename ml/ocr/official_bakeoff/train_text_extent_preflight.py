# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Build a deterministic train-only OCR text-extent treatment preflight."""

from __future__ import annotations

import argparse
from copy import deepcopy
from hashlib import sha256
import json
import math
import os
from pathlib import Path
import shutil
from typing import Any, Mapping, Sequence
import uuid

from PIL import Image, ImageDraw

from ml.synthetic import renderer
from ml.synthetic.dataset import PRESETS, _build_scenes, _scene_split
from ml.synthetic.fonts import FontResolver
from ml.synthetic.io import canonical_json_bytes, png_bytes
from ml.synthetic.runtime_graph_visible_content_v3 import render_visible_content_source


REPOSITORY_ROOT = Path(__file__).resolve().parents[3]
REPORT_SCHEMA = "graphreader.ocr-train-text-extent-preflight.v1"
TRUTH_SCHEMA = "graphreader.ocr-train-text-extent-truth.v1"
CAPTURE_TEMPLATE_SCHEMA = "graphreader.ocr-text-extent-app-capture-template.v1"
MANIFEST_SCHEMA = "graphreader.synthetic-runtime-raster-inputs.v1"
MANIFEST_SOURCE = "project-owned-synthetic-five-axis-family-v1"
TREATMENT_ID = "goal22-train-text-extent-v1"
EXPECTED_BINDING_SHA256 = "bcf821a2aaadc5ee8e18bd97dbe0488d120d2ac974a249c00d76b6cfccf0d98a"
EXPECTED_ORACLE_SHA256 = "ee14d67f651e20ec9479fb9e59ce1ea1ef77542b057f3d62312c476685520b0b"
EXPECTED_HISTORICAL_TRUTH_SHA256 = "829beb67695011f936b08272e69bb718b8b35d33bdb94c32a45f521f523fc3f5"
EXPECTED_DIAGNOSIS_SHA256 = "b09059f70c07aa7a770a1d21385c7e42340486d52f0d190362a1a55be7acd708"
EXPECTED_TEMPLATES_SHA256 = "f59733c5f0589bd1b9143d3c9ae83015b4c53ab593de229d56e46d9f11ec2f1c"
EXPECTED_CURRENT_SOURCE_SHA256_OVERRIDES = {
    "ml/synthetic/renderer.py": "c49c070afc505f92bfff7ff55b22e669c3ec2b76b56ff807b72a026ca0535283",
}
EXPECTED_TRAIN_SOURCE_COUNT = 20
EXPECTED_DEV_SOURCE_COUNT = 3
EXPECTED_TRAIN_TRUTH_COUNT = 709
EXPECTED_DEV_TRUTH_COUNT = 183
CONDITION_BAND = (32.0, 48.0)
LEGEND_BAND = (96.0, 112.0)
CONDITION_MINIMUM = 20
LEGEND_MINIMUM = 10
DATASET_SEEDS = (393, 394, 395, 396, 397)
TRAIN_CASE_COUNT_PER_DATASET_SEED = 4

_BINDING_PATH = Path(
    "artifacts/goal22-runs/marker-v25-plot-domain/visible-content-v3-binding/binding-v3.json"
)
_ORACLE_PATH = Path(
    "artifacts/goal22-runs/ocr-v39-pretrained-db-head/"
    "inverse-target-oracle-inputs-v1/request.json"
)
_HISTORICAL_TRUTH_PATH = _ORACLE_PATH.with_name("synthetic-truth.json")
_DIAGNOSIS_PATH = Path("docs/GOAL-22-V43-REPRESENTATION-DIAGNOSIS.json")

_CONDITION_LEXICON = (
    "Control",
    "Baseline",
    "Support",
    "Review",
    "Practice",
    "Followup",
    "General",
    "Monitor",
)
_LEGEND_LEXICON = (
    "Observed response",
    "Measured response",
    "Recorded outcome",
    "Session response",
    "Observed value",
    "Recorded value",
    "Session outcome",
    "Primary observation",
    "Treatment measure",
    "Response measure",
    "Observed outcome",
)


class TextExtentPreflightError(ValueError):
    """The authenticated train-only treatment failed a preflight invariant."""


def _sha(payload: bytes) -> str:
    return sha256(payload).hexdigest()


def _reject_duplicate_pairs(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise TextExtentPreflightError("JSON contains a duplicate key")
        result[key] = value
    return result


def _read_json(path: Path, expected_sha256: str | None = None) -> tuple[dict[str, Any], str]:
    try:
        payload = path.read_bytes()
    except OSError as error:
        raise TextExtentPreflightError("Required authenticated input is unavailable") from error
    digest = _sha(payload)
    if expected_sha256 is not None and digest != expected_sha256:
        raise TextExtentPreflightError("Required authenticated input hash changed")
    try:
        value = json.loads(payload, object_pairs_hook=_reject_duplicate_pairs)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise TextExtentPreflightError("Required authenticated input is malformed") from error
    if not isinstance(value, dict):
        raise TextExtentPreflightError("Required authenticated input is not an object")
    return value, digest


def _resolve_bound_path(root: Path, relative: object) -> Path:
    if not isinstance(relative, str) or not relative:
        raise TextExtentPreflightError("Authenticated input path is invalid")
    candidate = (root / Path(relative)).resolve()
    try:
        candidate.relative_to(root)
    except ValueError as error:
        raise TextExtentPreflightError("Authenticated input path escapes the repository") from error
    return candidate


def _repo_relative(root: Path, path: Path) -> str:
    try:
        return path.resolve().relative_to(root).as_posix()
    except ValueError as error:
        raise TextExtentPreflightError("Output must remain inside the repository") from error


def _family(scene: Mapping[str, Any]) -> str:
    return "|".join(
        f"{axis}={scene['families'][axis]['key']}"
        for axis in ("renderer", "font", "degradation", "template", "marker")
    )


def _records(annotation: Mapping[str, Any]) -> list[Mapping[str, Any]]:
    records = list(annotation.get("texts", []))
    for panel in annotation.get("panels", []):
        if isinstance(panel, Mapping):
            records.extend(panel.get("texts", []))
    return records


def _legends(panel: Mapping[str, Any]) -> list[Mapping[str, Any]]:
    value = panel.get("legends", panel.get("legend", []))
    if isinstance(value, Mapping):
        return [value]
    if isinstance(value, list):
        return [item for item in value if isinstance(item, Mapping)]
    return []


def _ltrb(record: Mapping[str, Any]) -> tuple[float, float, float, float]:
    raw = record.get("rendered_pixel_box")
    if not isinstance(raw, Sequence) or isinstance(raw, (str, bytes)) or len(raw) != 4:
        raise TextExtentPreflightError("Visible rendered text lacks pixel geometry")
    left, top, width, height = (float(value) for value in raw)
    right, bottom = left + width, top + height
    if not all(math.isfinite(value) for value in (left, top, right, bottom)):
        raise TextExtentPreflightError("Rendered text geometry is non-finite")
    if width <= 0 or height <= 0:
        raise TextExtentPreflightError("Rendered text geometry is empty")
    return left, top, right, bottom


def _overlap_pairs(records: Sequence[Mapping[str, Any]]) -> set[tuple[int, int]]:
    boxes = [_ltrb(record) for record in records]
    result: set[tuple[int, int]] = set()
    for left_index, left in enumerate(boxes):
        for right_index in range(left_index + 1, len(boxes)):
            right = boxes[right_index]
            if min(left[2], right[2]) > max(left[0], right[0]) and min(
                left[3], right[3]
            ) > max(left[1], right[1]):
                result.add((left_index, right_index))
    return result


def _without_text(scene: Mapping[str, Any]) -> dict[str, Any]:
    value = deepcopy(dict(scene))
    annotations = value.get("annotations")
    if isinstance(annotations, dict):
        annotations.pop("text_regions", None)
    for panel in value.get("panels", []):
        if not isinstance(panel, dict):
            continue
        for series in panel.get("series", []):
            if isinstance(series, dict):
                series.pop("display_name", None)
                series.pop("legend_text", None)
        for legend in _legends(panel):
            for entry in legend.get("entries", []):
                if isinstance(entry, dict):
                    entry.pop("text", None)
        for bar in panel.get("top_bars", panel.get("condition_bars", [])):
            if isinstance(bar, dict):
                bar.pop("label", None)
    return value


def _annotation_without_text(annotation: Mapping[str, Any]) -> dict[str, Any]:
    value = deepcopy(dict(annotation))
    containers = [value] + [
        panel for panel in value.get("panels", []) if isinstance(panel, dict)
    ]
    for container in containers:
        container.pop("texts", None)
        for series in container.get("series", []):
            if isinstance(series, dict):
                series.pop("display_name", None)
        for legend in container.get("legends", []):
            if isinstance(legend, dict):
                for entry in legend.get("entries", []):
                    if isinstance(entry, dict):
                        entry.pop("text", None)
    return value


def _text_record_without_extent(record: Mapping[str, Any]) -> dict[str, Any]:
    value = deepcopy(dict(record))
    for key in ("text", "text_id", "region_id", "box", "rendered_pixel_box"):
        value.pop(key, None)
    return value


def _measure_text(scene: Mapping[str, Any], text: str) -> tuple[float, float]:
    requested, size, search_paths = renderer._font_settings(scene)
    font = FontResolver(search_paths).resolve(requested, size).load()
    image = Image.new("RGB", (2048, 256), "white")
    record = renderer._draw_text(
        image,
        ImageDraw.Draw(image),
        {"text": text, "position": [32.0, 32.0], "anchor": "lt", "visible": True},
        font=font,
        default_position=(32.0, 32.0),
        default_role="other",
        default_id="extent-measurement",
    )
    left, top, right, bottom = _ltrb(record)
    return right - left, bottom - top


def _rotated(values: Sequence[str], seed: int, role: str) -> tuple[str, ...]:
    digest = sha256(f"{TREATMENT_ID}\n{seed}\n{role}".encode("utf-8")).digest()
    offset = int.from_bytes(digest[:2], "big") % len(values)
    return tuple(values[offset:]) + tuple(values[:offset])


def _choose_text(
    scene: Mapping[str, Any],
    candidates: Sequence[str],
    scale_x: float,
    band: tuple[float, float],
    maximum_source_width: float | None = None,
) -> tuple[str, float, float, float]:
    for text in candidates:
        width, height = _measure_text(scene, text)
        projected = width * scale_x
        if band[0] <= projected <= band[1] and (
            maximum_source_width is None or width <= maximum_source_width
        ):
            return text, width, height, projected
    raise TextExtentPreflightError("Train-only lexicon cannot satisfy the prescribed width band")


def _new_region_id(seed: int, old_id: str, role: str, text: str) -> str:
    return str(uuid.uuid5(uuid.NAMESPACE_URL, f"{TREATMENT_ID}\n{seed}\n{old_id}\n{role}\n{text}"))


def _apply_text_treatment(
    scene: Mapping[str, Any], *, scale_x: float, include_legend: bool,
    baseline_records: Sequence[Mapping[str, Any]] | None = None,
    condition_limit: int = 1,
) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    treated = deepcopy(dict(scene))
    regions = treated.get("annotations", {}).get("text_regions", [])
    if not isinstance(regions, list):
        raise TextExtentPreflightError("Generated scene has no text-region contract")
    seed = int(treated["seed"])
    selected: list[dict[str, Any]] = []
    requested_roles = ["condition_label"] + (["legend_text"] if include_legend else [])
    for role in requested_roles:
        candidates = [
            (index, item)
            for index, item in enumerate(regions)
            if isinstance(item, dict) and item.get("role") == role and item.get("visible") is not False
        ]
        if not candidates:
            raise TextExtentPreflightError("Generated train source lacks a prescribed treatment role")
        lexicon = _CONDITION_LEXICON if role == "condition_label" else _LEGEND_LEXICON
        band = CONDITION_BAND if role == "condition_label" else LEGEND_BAND
        limit = condition_limit if role == "condition_label" else 1
        role_selected = 0
        for index, region in reversed(candidates) if role == "condition_label" else candidates:
            if role_selected >= limit:
                break
            maximum_source_width = (
                _legend_available_width(treated, region, str(region["text"]))
                if role == "legend_text"
                else None
            )
            text, width, height, projected = _choose_text(
                treated,
                _rotated(lexicon, seed, role),
                scale_x,
                band,
                maximum_source_width,
            )
            if baseline_records is not None:
                own = next((r for r in baseline_records if r.get("region_id") == region["region_id"]), None)
                if own is None:
                    raise TextExtentPreflightError("Treatment region lacks rendered source evidence")
                left, top, _, _ = _ltrb(own)
                proposed = (left, top, left + width, top + height)
                def intersects(box):
                    return min(proposed[2], box[2]) > max(proposed[0], box[0]) and min(proposed[3], box[3]) > max(proposed[1], box[1])
                if any(intersects(_ltrb(r)) for r in baseline_records if r is not own):
                    continue
            old_id = str(region["region_id"])
            old_text = str(region["text"])
            region["text"] = text
            region["region_id"] = _new_region_id(seed, old_id, role, text)
            box = list(region["box"])
            box[2] = round(width, 4)
            box[3] = round(height, 4)
            region["box"] = box
            _update_declarative_text(treated, region, old_text, text)
            selected.append(
                {
                    "region_index": index,
                    "old_region_id": old_id,
                    "region_id": region["region_id"],
                    "role": role,
                    "text": text,
                    "measured_source_width": width,
                    "historical_projected_tensor_width": projected,
                }
            )
            role_selected += 1
    return treated, selected


def _update_declarative_text(
    scene: dict[str, Any], region: Mapping[str, Any], old_text: str, new_text: str
) -> None:
    panel = next(
        (item for item in scene.get("panels", []) if item.get("panel_id") == region.get("panel_id")),
        None,
    )
    if not isinstance(panel, dict):
        raise TextExtentPreflightError("Treatment text refers to an unknown panel")
    if region.get("role") == "legend_text":
        source_box = [float(value) for value in region["box"]]
        _, entry, maximum_width = _legend_binding(scene, region, old_text)
        if source_box[2] > maximum_width:
            raise TextExtentPreflightError("Legend treatment exceeds its existing frame")
        entry["text"] = new_text
        series_id = entry.get("series_id")
        series = next((item for item in panel.get("series", []) if item.get("series_id") == series_id), None)
        if not isinstance(series, dict):
            raise TextExtentPreflightError("Legend treatment could not bind its series")
        series["display_name"] = new_text
        series["legend_text"] = new_text
    elif region.get("role") == "condition_label":
        bars = [
            item
            for item in panel.get("top_bars", panel.get("condition_bars", []))
            if isinstance(item, dict) and str(item.get("label")) == old_text
        ]
        if bars:
            source_x = float(region["box"][0])
            bars.sort(key=lambda item: abs((float(item["line"][0]) + float(item["line"][2])) / 2 - source_x))
            bars[0]["label"] = new_text


def _legend_binding(
    scene: Mapping[str, Any], region: Mapping[str, Any], text: str
) -> tuple[Mapping[str, Any], dict[str, Any], float]:
    panel = next(
        (item for item in scene.get("panels", []) if item.get("panel_id") == region.get("panel_id")),
        None,
    )
    if not isinstance(panel, Mapping):
        raise TextExtentPreflightError("Legend treatment refers to an unknown panel")
    source_box = [float(value) for value in region["box"]]
    matches: list[tuple[Mapping[str, Any], dict[str, Any]]] = []
    for legend in _legends(panel):
        for entry in legend.get("entries", []):
            box = entry.get("text_box")
            if isinstance(entry, dict) and isinstance(box, list) and len(box) == 4 and all(
                abs(float(box[index]) - source_box[index]) < 0.001 for index in (0, 1)
            ) and str(entry.get("text")) == text:
                matches.append((legend, entry))
    if len(matches) != 1:
        raise TextExtentPreflightError("Legend treatment could not bind declarative text")
    legend, entry = matches[0]
    frame = legend.get("box")
    if not isinstance(frame, list) or len(frame) != 4:
        raise TextExtentPreflightError("Legend treatment lacks an existing frame")
    maximum_width = float(frame[0]) + float(frame[2]) - source_box[0]
    if maximum_width <= 0:
        raise TextExtentPreflightError("Legend treatment has no room in its existing frame")
    return panel, entry, maximum_width


def _legend_available_width(
    scene: Mapping[str, Any], region: Mapping[str, Any], text: str
) -> float:
    return _legend_binding(scene, region, text)[2]


def _inverse_affine(matrix: Sequence[object]) -> tuple[float, ...]:
    if len(matrix) != 9:
        raise TextExtentPreflightError("Historical panel transform is malformed")
    values = tuple(float(item) for item in matrix)
    a, b, c, d, e, f, g, h, i = values
    if abs(g) > 1e-12 or abs(h) > 1e-12 or abs(i - 1.0) > 1e-12:
        raise TextExtentPreflightError("Historical panel transform is not affine")
    determinant = a * e - b * d
    if abs(determinant) <= 1e-12:
        raise TextExtentPreflightError("Historical panel transform is singular")
    return (
        e / determinant,
        -b / determinant,
        (b * f - e * c) / determinant,
        -d / determinant,
        a / determinant,
        (d * c - a * f) / determinant,
        0.0,
        0.0,
        1.0,
    )


def _transform_box(box: Sequence[float], matrix: Sequence[float]) -> tuple[float, float, float, float]:
    points = []
    for x, y in ((box[0], box[1]), (box[2], box[1]), (box[2], box[3]), (box[0], box[3])):
        points.append((matrix[0] * x + matrix[1] * y + matrix[2], matrix[3] * x + matrix[4] * y + matrix[5]))
    return min(x for x, _ in points), min(y for _, y in points), max(x for x, _ in points), max(y for _, y in points)


def _historical_projections(
    source_box: Sequence[float], panels: Sequence[Mapping[str, Any]]
) -> list[dict[str, Any]]:
    result: list[dict[str, Any]] = []
    for panel in panels:
        inverse = _inverse_affine(panel["panel_to_source_matrix"])
        box = _transform_box(source_box, inverse)
        width, height = int(panel["width"]), int(panel["height"])
        clipped = (
            max(0.0, box[0]),
            max(0.0, box[1]),
            min(float(width), box[2]),
            min(float(height), box[3]),
        )
        if clipped[2] <= clipped[0] or clipped[3] <= clipped[1]:
            continue
        status = "full" if all(abs(box[index] - clipped[index]) <= 1e-9 for index in range(4)) else "partial"
        scale_x = int(panel["tensor_width"]) / float(width)
        scale_y = int(panel["tensor_height"]) / float(height)
        tensor = [
            clipped[0] * scale_x,
            clipped[1] * scale_y,
            clipped[2] * scale_x,
            clipped[3] * scale_y,
        ]
        result.append(
            {
                "historical_panel_id": str(panel["panel_id"]),
                "status": status,
                "panel_box_ltrb": [round(value, 6) for value in clipped],
                "tensor_box_ltrb": [round(value, 6) for value in tensor],
                "tensor_width": round(tensor[2] - tensor[0], 6),
                "basis": "authenticated_historical_crop_geometry_not_current_app_mapping",
            }
        )
    return result


def _authenticated_history(root: Path) -> dict[str, Any]:
    diagnosis, diagnosis_sha = _read_json(
        root / _DIAGNOSIS_PATH, EXPECTED_DIAGNOSIS_SHA256
    )
    if diagnosis.get("schema") != "graphreader.goal22.v43-representation-diagnosis.v1":
        raise TextExtentPreflightError("Representation diagnosis schema changed")
    if not isinstance(diagnosis.get("lead_verification", {}).get("policy_clarification"), str):
        raise TextExtentPreflightError("Representation diagnosis lacks its policy clarification")
    binding, binding_sha = _read_json(root / _BINDING_PATH, EXPECTED_BINDING_SHA256)
    oracle, oracle_sha = _read_json(root / _ORACLE_PATH, EXPECTED_ORACLE_SHA256)
    historical_truth, truth_sha = _read_json(
        root / _HISTORICAL_TRUTH_PATH, EXPECTED_HISTORICAL_TRUTH_SHA256
    )
    if oracle.get("synthetic_truth", {}).get("sha256") != truth_sha:
        raise TextExtentPreflightError("Historical oracle does not bind historical truth")

    historical_sources_by_seed: dict[int, dict[str, Any]] = {}
    train_manifest_descriptors: list[dict[str, Any]] = []
    for descriptor in binding.get("train", []):
        manifest_path = _resolve_bound_path(root, descriptor.get("manifest_path"))
        manifest, manifest_sha = _read_json(manifest_path, descriptor.get("manifest_sha256"))
        if manifest.get("schema") != MANIFEST_SCHEMA or manifest.get("split") != "train":
            raise TextExtentPreflightError("Historical train manifest contract changed")
        for image in manifest.get("images", []):
            image_path = (manifest_path.parent / str(image.get("image"))).resolve()
            expected = image.get("image_sha256")
            if _sha(image_path.read_bytes()) != expected:
                raise TextExtentPreflightError("Historical train image hash changed")
            seed = int(image["seed"])
            if seed in historical_sources_by_seed:
                raise TextExtentPreflightError("Historical train source seed repeats")
            historical_sources_by_seed[seed] = dict(image)
        train_manifest_descriptors.append(
            {"path": _repo_relative(root, manifest_path), "sha256": manifest_sha}
        )
    if len(historical_sources_by_seed) != EXPECTED_TRAIN_SOURCE_COUNT:
        raise TextExtentPreflightError("Historical train source denominator changed")

    dev_descriptor = binding.get("dev", {})
    dev_manifest_path = _resolve_bound_path(root, dev_descriptor.get("manifest_path"))
    dev_manifest, dev_manifest_sha = _read_json(
        dev_manifest_path, dev_descriptor.get("manifest_sha256")
    )
    dev_source_ids: list[str] = []
    for image in dev_manifest.get("images", []):
        image_path = (dev_manifest_path.parent / str(image.get("image"))).resolve()
        expected = str(image.get("image_sha256"))
        if _sha(image_path.read_bytes()) != expected:
            raise TextExtentPreflightError("Historical development image hash changed")
        dev_source_ids.append(expected)
    if len(dev_source_ids) != EXPECTED_DEV_SOURCE_COUNT:
        raise TextExtentPreflightError("Historical development source denominator changed")
    dev_truths = [item for item in historical_truth.get("truths", []) if item.get("split") == "validation"]
    if len(dev_truths) != EXPECTED_DEV_TRUTH_COUNT or {str(item["source_sha256"]) for item in dev_truths} != set(dev_source_ids):
        raise TextExtentPreflightError("Historical development truth descriptor changed")

    panels_by_source: dict[str, list[dict[str, Any]]] = {}
    for panel in oracle.get("panels", []):
        if panel.get("split") == "train":
            panels_by_source.setdefault(str(panel["source_sha256"]), []).append(dict(panel))
    if set(panels_by_source) != {str(value["image_sha256"]) for value in historical_sources_by_seed.values()}:
        raise TextExtentPreflightError("Historical train panel mapping source set changed")
    return {
        "diagnosis": {"path": _DIAGNOSIS_PATH.as_posix(), "sha256": diagnosis_sha},
        "binding": {"path": _BINDING_PATH.as_posix(), "sha256": binding_sha},
        "train_manifests": train_manifest_descriptors,
        "dev_manifest": {"path": _repo_relative(root, dev_manifest_path), "sha256": dev_manifest_sha},
        "oracle": {"path": _ORACLE_PATH.as_posix(), "sha256": oracle_sha},
        "historical_truth": {"path": _HISTORICAL_TRUTH_PATH.as_posix(), "sha256": truth_sha},
        "dev_source_ids": sorted(dev_source_ids),
        "historical_sources_by_seed": historical_sources_by_seed,
        "panels_by_source": panels_by_source,
        "binding_sources": binding.get("generator_profile", {}).get("sources", []),
    }


def _current_source_inventory(root: Path, bound: Sequence[Mapping[str, Any]]) -> list[dict[str, Any]]:
    result: list[dict[str, Any]] = []
    seen: set[str] = set()
    for descriptor in bound:
        relative = str(descriptor["relative_path"])
        seen.add(relative)
        path = _resolve_bound_path(root, relative)
        current = _sha(path.read_bytes())
        historical = str(descriptor["sha256"])
        expected_current = EXPECTED_CURRENT_SOURCE_SHA256_OVERRIDES.get(relative, historical)
        if current != expected_current:
            raise TextExtentPreflightError("Authenticated current generator source changed")
        result.append(
            {
                "path": relative,
                "sha256": current,
                "authenticated_current_sha256": expected_current,
                "historical_binding_sha256": historical,
                "matches_historical_binding": current == historical,
            }
        )
    if set(EXPECTED_CURRENT_SOURCE_SHA256_OVERRIDES) - seen:
        raise TextExtentPreflightError("Current generator source override is not historically bound")
    if not any(
        item["path"] == "ml/synthetic/templates.py"
        and item["sha256"] == EXPECTED_TEMPLATES_SHA256
        for item in result
    ):
        raise TextExtentPreflightError("Frozen train family and seed schedule source changed")
    own_path = Path(__file__).resolve()
    result.append({"path": _repo_relative(root, own_path), "sha256": _sha(own_path.read_bytes())})
    return result


def _build_train_scenes(dataset_seed: int) -> list[dict[str, Any]]:
    train_specs = PRESETS["smoke"][:TRAIN_CASE_COUNT_PER_DATASET_SEED]
    scenes = _build_scenes(train_specs, dataset_seed, require_complete_style_catalog=False)
    expected_seeds = {
        dataset_seed * 100 + index for index in range(TRAIN_CASE_COUNT_PER_DATASET_SEED)
    }
    if (
        len(scenes) != TRAIN_CASE_COUNT_PER_DATASET_SEED
        or {_scene_split(scene) for scene in scenes} != {"train"}
        or {int(scene["seed"]) for scene in scenes} != expected_seeds
    ):
        raise TextExtentPreflightError("Train scene schedule changed")
    return scenes


def _build_payloads(root: Path, output: Path) -> tuple[dict[str, bytes], dict[str, Any]]:
    history = _authenticated_history(root)
    current_sources = _current_source_inventory(root, history["binding_sources"])
    generated: dict[str, bytes] = {}
    source_inventory: list[dict[str, Any]] = []
    truths: list[dict[str, Any]] = []
    total_baseline = 0
    baseline_overlap_count = 0
    treatment_overlap_count = 0
    condition_selected = 0
    legend_selected = 0

    for dataset_seed in DATASET_SEEDS:
        train_scenes = _build_train_scenes(dataset_seed)
        manifest_images: list[dict[str, Any]] = []
        for scene in train_scenes:
            seed = int(scene["seed"])
            historical = history["historical_sources_by_seed"].get(seed)
            if historical is None:
                raise TextExtentPreflightError("Generated train seed is absent from the frozen source binding")
            family = _family(scene)
            width = int(scene["canvas"]["width"])
            height = int(scene["canvas"]["height"])
            if family != historical.get("family") or width != int(historical["width"]) or height != int(historical["height"]):
                raise TextExtentPreflightError("Train family or canvas schedule changed")
            old_source_id = str(historical["image_sha256"])
            historical_panels = history["panels_by_source"][old_source_id]
            scales = {int(panel["tensor_width"]) / float(panel["width"]) for panel in historical_panels}
            if len(scales) != 1:
                raise TextExtentPreflightError("Historical crop geometry has inconsistent horizontal scales")
            scale_x = scales.pop()

            baseline_result = render_visible_content_source(scene)
            if _sha(png_bytes(baseline_result.image.convert("RGB"))) != old_source_id:
                raise TextExtentPreflightError(
                    "Current authenticated renderer changed a historical train raster"
                )
            baseline_records = [record for record in _records(baseline_result.annotation) if record.get("visible") is not False and str(record.get("text", "")).strip()]
            total_baseline += len(baseline_records)
            baseline_pairs = _overlap_pairs(baseline_records)
            baseline_overlap_count += len(baseline_pairs)

            treated_scene, selected = _apply_text_treatment(
                scene,
                scale_x=scale_x,
                include_legend=legend_selected < LEGEND_MINIMUM,
                baseline_records=baseline_records,
                condition_limit=max(0, CONDITION_MINIMUM - condition_selected),
            )
            treated_result = render_visible_content_source(treated_scene)
            treated_records = [record for record in _records(treated_result.annotation) if record.get("visible") is not False and str(record.get("text", "")).strip()]
            if len(treated_records) != len(baseline_records):
                raise TextExtentPreflightError("Text truth count changed during treatment")
            if _without_text(scene) != _without_text(treated_scene):
                raise TextExtentPreflightError("Treatment changed non-text scene geometry")
            if _annotation_without_text(baseline_result.annotation) != _annotation_without_text(treated_result.annotation):
                raise TextExtentPreflightError("Treatment changed non-text rendered geometry")
            if baseline_result.marker_mask.tobytes() != treated_result.marker_mask.tobytes():
                raise TextExtentPreflightError("Treatment changed rendered marker geometry")
            treated_pairs = _overlap_pairs(treated_records)
            treatment_overlap_count += len(treated_pairs)
            if treated_pairs - baseline_pairs:
                details = [
                    [(treated_records[index].get("role"), treated_records[index].get("rendered_pixel_box"))
                     for index in pair]
                    for pair in sorted(treated_pairs - baseline_pairs)
                ]
                raise TextExtentPreflightError(
                    f"Treatment introduced rendered-text overlap in train seed {seed}: {details}"
                )
            selected_ids = {item["region_id"] for item in selected}
            selected_records = {str(record["region_id"]): record for record in treated_records if str(record["region_id"]) in selected_ids}
            if set(selected_records) != selected_ids:
                raise TextExtentPreflightError("Treatment text did not render exactly once")
            baseline_by_id = {str(record["region_id"]): record for record in baseline_records}
            treated_by_id = {str(record["region_id"]): record for record in treated_records}
            if len(baseline_by_id) != len(baseline_records) or len(treated_by_id) != len(treated_records):
                raise TextExtentPreflightError("Rendered text region identity repeats")
            old_selected_ids = {item["old_region_id"] for item in selected}
            unchanged_ids = set(baseline_by_id) - old_selected_ids
            if any(baseline_by_id[item] != treated_by_id.get(item) for item in unchanged_ids):
                raise TextExtentPreflightError("Treatment changed untreated rendered text")
            for selection in selected:
                baseline_record = baseline_by_id[selection["old_region_id"]]
                treated_record = selected_records[selection["region_id"]]
                if (
                    _text_record_without_extent(baseline_record)
                    != _text_record_without_extent(treated_record)
                ):
                    raise TextExtentPreflightError("Treatment changed selected text layout")
                box = _ltrb(selected_records[selection["region_id"]])
                if box[0] <= 0 or box[1] <= 0 or box[2] >= width or box[3] >= height:
                    raise TextExtentPreflightError("Treatment text is clipped by the source canvas")
                projected = (box[2] - box[0]) * scale_x
                band = CONDITION_BAND if selection["role"] == "condition_label" else LEGEND_BAND
                if not band[0] <= projected <= band[1]:
                    raise TextExtentPreflightError("Final rendered treatment width left its prescribed band")
                selection["rendered_source_box_ltrb"] = [round(value, 6) for value in box]
                selection["historical_projected_tensor_width"] = round(projected, 6)
                if selection["role"] == "condition_label":
                    condition_selected += 1
                else:
                    legend_selected += 1

            image_payload = png_bytes(treated_result.image.convert("RGB"))
            image_sha = _sha(image_payload)
            base = f"sources/train{dataset_seed}"
            image_name = f"train-{seed}-{image_sha[:12]}.png"
            image_rel = f"{base}/{image_name}"
            scene_rel = f"{base}/train-{seed}.scene.json"
            annotation_rel = f"{base}/train-{seed}.annotation.json"
            scene_payload = canonical_json_bytes(treated_scene)
            annotation_payload = canonical_json_bytes(treated_result.annotation)
            generated[image_rel] = image_payload
            generated[scene_rel] = scene_payload
            generated[annotation_rel] = annotation_payload

            manifest_images.append(
                {
                    "image": image_name,
                    "image_sha256": image_sha,
                    "width": width,
                    "height": height,
                    "split": "train",
                    "family": family,
                    "seed": seed,
                }
            )
            selection_by_id = {item["region_id"]: item for item in selected}
            for record in treated_records:
                box = _ltrb(record)
                projections = _historical_projections(box, historical_panels)
                truth_id = _sha(f"{image_sha}\n{record['text_id']}".encode("utf-8"))
                truths.append(
                    {
                        "truth_id": truth_id,
                        "source_id": image_sha,
                        "source_sha256": image_sha,
                        "text_id": str(record["text_id"]),
                        "region_id": str(record["region_id"]),
                        "panel_id": str(record.get("panel_id", "")),
                        "role": str(record["role"]),
                        "text": str(record["text"]),
                        "source_box_ltrb": [round(value, 6) for value in box],
                        "coordinate_space": "original_pixels",
                        "treatment_role": selection_by_id.get(str(record["region_id"]), {}).get("role"),
                        "historical_projections": projections,
                    }
                )
            source_inventory.append(
                {
                    "source_id": image_sha,
                    "source_sha256": image_sha,
                    "historical_source_sha256": old_source_id,
                    "image": _repo_relative(root, output / image_rel),
                    "image_sha256": image_sha,
                    "scene": _repo_relative(root, output / scene_rel),
                    "scene_sha256": _sha(scene_payload),
                    "annotation": _repo_relative(root, output / annotation_rel),
                    "annotation_sha256": _sha(annotation_payload),
                    "seed": seed,
                    "dataset_seed": dataset_seed,
                    "split": "train",
                    "family": family,
                    "width": width,
                    "height": height,
                    "text_truth_count": len(treated_records),
                    "selected_treatments": selected,
                }
            )
        manifest = {
            "schema": MANIFEST_SCHEMA,
            "source": MANIFEST_SOURCE,
            "preset": "smoke",
            "seed": dataset_seed,
            "split": "train",
            "contains_truth": False,
            "contains_precomputed_masks": False,
            "images": manifest_images,
        }
        manifest_rel = f"sources/train{dataset_seed}/input-manifest.json"
        manifest_payload = canonical_json_bytes(manifest)
        generated[manifest_rel] = manifest_payload
        manifest_path = _repo_relative(root, output / manifest_rel)
        for source in source_inventory[-4:]:
            source["manifest"] = manifest_path
            source["manifest_sha256"] = _sha(manifest_payload)

    if total_baseline != EXPECTED_TRAIN_TRUTH_COUNT or len(truths) != EXPECTED_TRAIN_TRUTH_COUNT:
        raise TextExtentPreflightError("Train text truth denominator changed")
    if condition_selected < CONDITION_MINIMUM or legend_selected < LEGEND_MINIMUM:
        raise TextExtentPreflightError("Train treatment coverage is incomplete")
    if len({item["truth_id"] for item in truths}) != len(truths):
        raise TextExtentPreflightError("Generated train truth identities repeat")
    if len({item["source_id"] for item in source_inventory}) != EXPECTED_TRAIN_SOURCE_COUNT:
        raise TextExtentPreflightError("Generated train source identities repeat")
    if {item["source_id"] for item in source_inventory} & set(history["dev_source_ids"]):
        raise TextExtentPreflightError("Generated train source identity overlaps historical development")

    truth_document = {
        "schema": TRUTH_SCHEMA,
        "treatment_id": TREATMENT_ID,
        "split": "train",
        "source_count": len(source_inventory),
        "truth_count": len(truths),
        "coordinate_space": "original_pixels",
        "truths": truths,
        "private_data": False,
        "sealed_data": False,
        "synthetic_only": True,
    }
    truth_payload = canonical_json_bytes(truth_document)
    generated["train-text-truth.json"] = truth_payload
    report = {
        "schema": REPORT_SCHEMA,
        "status": "preflight_complete_app_mapping_pending",
        "treatment_id": TREATMENT_ID,
        "scope": "project-owned train-only rendered-text extent treatment",
        "candidate_opened": False,
        "optimizer_steps": 0,
        "model_inference": False,
        "private_data": False,
        "sealed_data": False,
        "synthetic_only": True,
        "train_text_truth": {
            "path": _repo_relative(root, output / "train-text-truth.json"),
            "sha256": _sha(truth_payload),
        },
        "sources": source_inventory,
        "historical_train": {
            "binding": history["binding"],
            "manifests": history["train_manifests"],
            "source_count": EXPECTED_TRAIN_SOURCE_COUNT,
        },
        "historical_dev": {
            "binding": history["binding"],
            "manifest": history["dev_manifest"],
            "oracle_request": history["oracle"],
            "synthetic_truth": history["historical_truth"],
            "source_ids": history["dev_source_ids"],
            "source_count": EXPECTED_DEV_SOURCE_COUNT,
            "truth_count": EXPECTED_DEV_TRUTH_COUNT,
            "scene_regenerations": 0,
            "pixel_regenerations": 0,
            "truth_regenerations": 0,
        },
        "source_provenance": {
            "diagnosis": history["diagnosis"],
            "current_generator_sources": current_sources,
        },
        "invariants": {
            "seed_schedule_preserved": True,
            "family_profiles_preserved": True,
            "canvas_schedule_preserved": True,
            "nontext_scene_geometry_preserved": True,
            "nontext_rendered_geometry_preserved": True,
            "marker_mask_preserved": True,
            "untreated_text_layout_preserved": True,
            "selected_text_non_extent_layout_preserved": True,
            "legend_treatments_fit_existing_frames": True,
            "current_generator_sources_authenticated": True,
            "current_baseline_rasters_match_historical_train": True,
            "generated_train_sources_disjoint_from_historical_dev": True,
            "full_train_truth_saved": True,
            "baseline_text_truth_count": total_baseline,
            "treatment_text_truth_count": len(truths),
            "baseline_rendered_text_overlap_pairs": baseline_overlap_count,
            "treatment_rendered_text_overlap_pairs": treatment_overlap_count,
            "introduced_rendered_text_overlap_pairs": 0,
            "clipped_treatment_text_count": 0,
        },
        "treatment_coverage": {
            "basis": "authenticated_historical_crop_geometry_not_current_app_mapping",
            "accuracy_gate": False,
            "condition_label": {
                "target_tensor_width_band": list(CONDITION_BAND),
                "minimum_count": CONDITION_MINIMUM,
                "observed_count": condition_selected,
            },
            "legend_text": {
                "target_tensor_width_band": list(LEGEND_BAND),
                "minimum_count": LEGEND_MINIMUM,
                "observed_count": legend_selected,
            },
        },
        "actual_app_mapping": {
            "status": "pending_external_csharp_capture",
            "claim_made": False,
        },
        "limitations": [
            "Historical tensor widths reuse authenticated prior crop geometry only.",
            "Actual new-image application crops and transforms require the downstream C# capture.",
            "This preflight does not authorize a model candidate, optimizer step, or accuracy claim.",
        ],
    }
    report_payload = canonical_json_bytes(report)
    generated["preflight-report.json"] = report_payload
    capture_template = {
        "schema": CAPTURE_TEMPLATE_SCHEMA,
        "binding": {
            "path": _repo_relative(root, output / "preflight-report.json"),
            "sha256": _sha(report_payload),
        },
        "split": "train",
        "source_count": len(source_inventory),
        "sources": [
            {
                key: source[key]
                for key in (
                    "source_id",
                    "source_sha256",
                    "image",
                    "image_sha256",
                    "manifest",
                    "manifest_sha256",
                    "seed",
                    "dataset_seed",
                    "family",
                    "width",
                    "height",
                )
            }
            for source in source_inventory
        ],
        "contains_truth": False,
        "contains_text": False,
        "model_inference": False,
    }
    generated["capture-request-template.json"] = canonical_json_bytes(capture_template)
    return generated, report


def run_preflight(
    repository_root: Path = REPOSITORY_ROOT,
    output_directory: Path | None = None,
) -> dict[str, Any]:
    """Authenticate history and publish a deterministic train-only preflight."""

    root = Path(repository_root).resolve()
    output = (
        Path(output_directory).resolve()
        if output_directory is not None
        else root / "artifacts/goal22-runs/ocr-text-extent-preflight"
    )
    _repo_relative(root, output)
    payloads, report = _build_payloads(root, output)
    temporary = output.with_name(output.name + ".publishing")
    if output.exists() or temporary.exists():
        raise TextExtentPreflightError("Preflight output already exists")
    temporary.mkdir(parents=True)
    try:
        for relative, payload in sorted(payloads.items()):
            path = temporary / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(payload)
        os.replace(temporary, output)
    except BaseException:
        if temporary.exists():
            shutil.rmtree(temporary)
        raise
    return report


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository-root", type=Path, default=REPOSITORY_ROOT)
    parser.add_argument("--output-directory", type=Path)
    arguments = parser.parse_args()
    report = run_preflight(arguments.repository_root, arguments.output_directory)
    print(canonical_json_bytes({"status": report["status"], "source_count": len(report["sources"])}).decode("utf-8"), end="")


if __name__ == "__main__":
    main()
