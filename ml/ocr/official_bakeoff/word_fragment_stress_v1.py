# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Prepare and score a bounded train-only OCR word-fragment stress diagnostic."""

from __future__ import annotations

import argparse
from collections import Counter
from copy import deepcopy
from hashlib import sha256
import json
import math
from pathlib import Path
import statistics
import sys
from typing import Any, Iterable, Mapping, Sequence
import uuid


ROOT = Path(__file__).resolve().parents[3]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from ml.synthetic.dataset import PRESETS, _build_scenes, _scene_split  # noqa: E402
from ml.synthetic.io import canonical_json_bytes, png_bytes  # noqa: E402
from ml.synthetic.runtime_graph_visible_content_v3 import (  # noqa: E402
    render_visible_content_source,
)
from ml.synthetic.schema import validate_scene  # noqa: E402


EXPECTED_OUTCOME_SHA256 = "0fc4420747c3b7abf3259ffb9233676d6bc8c291dc54342b8571f335461d9024"
OUTCOME_PATH = ROOT / "ml/ocr/text_extent_db_head_v44/P1_RESULT.json"
EXPECTED_PARENT_CANDIDATE_SHA256 = "bee0da6bd9041769b4add04f21d0bd8530ae724b6b890d0f1f2dc376a7db9e01"
EXPECTED_DETECTOR_SHA256 = "615495cc882b6ae8633081d89c3ab08c0d4a80a04aa36bbde4157fc7828f5923"
EXPECTED_RECOGNIZER_SHA256 = "7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743"
TOOL_ASSEMBLY = "GraphReader.SyntheticRuntimeEvidence"
SCHEMA_PREFIX = "graphreader.word-fragment-stress-v1"
ROLE_CASES = (
    ("participant", ("Participant", "01"), ("Observer", "02")),
    ("legend_text", ("Primary", "outcome"), ("Baseline", "Treatment")),
    ("annotation", ("Follow", "up"), ("Probe", "Session")),
    ("phase_heading", ("Withdrawal", "continued"), ("Phase", "Change")),
)
GAP_CONFIGURATIONS = (
    (0.25, 0.00),
    (0.50, 0.00),
    (0.75, 0.00),
    (1.00, 0.00),
    (1.25, 0.10),
    (1.50, 0.10),
    (2.00, 0.20),
    (2.50, 0.20),
)
GENERATOR_SOURCES = (
    "ml/synthetic/dataset.py",
    "ml/synthetic/fonts.py",
    "ml/synthetic/io.py",
    "ml/synthetic/renderer.py",
    "ml/synthetic/runtime_graph_visible_content_v3.py",
    "ml/synthetic/schema.py",
    "ml/synthetic/templates.py",
    "ml/ocr/official_bakeoff/word_fragment_stress_v1.py",
)


def _sha_bytes(payload: bytes) -> str:
    return sha256(payload).hexdigest()


def _sha(path: Path) -> str:
    return _sha_bytes(path.read_bytes())


def _read(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def _inside_root(value: str | Path) -> Path:
    path = (ROOT / value).resolve() if not Path(value).is_absolute() else Path(value).resolve()
    if path != ROOT and ROOT not in path.parents:
        raise RuntimeError(f"Path escaped repository: {value}")
    return path


def _inside_artifacts(value: str | Path) -> Path:
    path = _inside_root(value)
    artifacts = (ROOT / "artifacts").resolve()
    if path == artifacts or artifacts not in path.parents:
        raise RuntimeError(f"Path must be below artifacts: {value}")
    return path


def _verify(path: Path, expected: str, label: str) -> None:
    if not path.is_file() or _sha(path) != expected:
        raise RuntimeError(f"{label} identity changed: {path}")


def _repository_path(path: Path) -> str:
    return path.resolve().relative_to(ROOT).as_posix()


def _write_new(path: Path, payload: bytes) -> str:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("xb") as stream:
        stream.write(payload)
    return _sha_bytes(payload)


def _font_provenance(annotation: Mapping[str, Any]) -> dict[str, Any]:
    font = annotation.get("font")
    if not isinstance(font, Mapping):
        raise RuntimeError("Stress render lacks font provenance")
    required = {
        "requested", "resolved_file", "resolved_path", "family", "style",
        "size_px", "source", "sha256", "bundled",
    }
    if set(font) != required or font.get("source") != "system" or font.get("bundled") is not False:
        raise RuntimeError("Stress render did not use one non-bundled system font")
    resolved_path = Path(str(font["resolved_path"])).resolve()
    expected_sha = str(font["sha256"])
    _verify(resolved_path, expected_sha, "stress system font")
    return {
        **dict(font),
        "license_and_distribution_status": (
            "Host-installed system dependency; bytes are verified for reproducibility "
            "but are not copied, bundled, committed, or redistributed."
        ),
    }


def _records(annotation: Mapping[str, Any]) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    rows.extend(row for row in annotation.get("texts", []) if isinstance(row, dict))
    for panel in annotation.get("panels", []):
        if isinstance(panel, Mapping):
            rows.extend(row for row in panel.get("texts", []) if isinstance(row, dict))
    return rows


def _ltrb(record: Mapping[str, Any]) -> tuple[float, float, float, float]:
    box = record.get("rendered_pixel_box")
    if not isinstance(box, Sequence) or len(box) != 4:
        raise RuntimeError("Rendered stress token lacks a pixel box")
    left, top, width, height = (float(value) for value in box)
    if not all(math.isfinite(value) for value in (left, top, width, height)) or width <= 0 or height <= 0:
        raise RuntimeError("Rendered stress token has invalid geometry")
    return left, top, left + width, top + height


def _union(boxes: Sequence[Sequence[float]]) -> tuple[float, float, float, float]:
    return (
        min(float(box[0]) for box in boxes),
        min(float(box[1]) for box in boxes),
        max(float(box[2]) for box in boxes),
        max(float(box[3]) for box in boxes),
    )


def _stable_id(*parts: object) -> str:
    return str(uuid.uuid5(uuid.NAMESPACE_URL, "\n".join(str(part) for part in parts)))


def _family(scene: Mapping[str, Any]) -> str:
    return "|".join(
        f"{axis}={scene['families'][axis]['key']}"
        for axis in ("renderer", "font", "degradation", "template", "marker")
    )


def _token_record(
    panel_id: str,
    region_id: str,
    role: str,
    text: str,
    x: float,
    y: float,
) -> dict[str, Any]:
    return {
        "box": [round(x, 6), round(y, 6), 180.0, 24.0],
        "panel_id": panel_id,
        "region_id": region_id,
        "role": role,
        "text": text,
        "visible": True,
    }


def _render_stress_scene(
    source_scene: Mapping[str, Any],
    source_index: int,
    role: str,
    positive_tokens: tuple[str, str],
    negative_tokens: tuple[str, str],
    gap_height_ratio: float,
    vertical_offset_height_ratio: float,
) -> tuple[dict[str, Any], Any, dict[str, Any]]:
    scene = deepcopy(source_scene)
    panel_id = str(scene["panels"][0]["panel_id"])
    identifiers = {
        key: _stable_id("word-fragment-stress-v1", source_index, key)
        for key in ("positive-left", "positive-right", "negative-left", "negative-right")
    }
    regions = [
        _token_record(panel_id, identifiers["positive-left"], role, positive_tokens[0], 190.0, 52.0),
        _token_record(panel_id, identifiers["positive-right"], role, positive_tokens[1], 360.0, 52.0),
        _token_record(panel_id, identifiers["negative-left"], role, negative_tokens[0], 650.0, 52.0),
        _token_record(panel_id, identifiers["negative-right"], role, negative_tokens[1], 820.0, 52.0),
    ]
    scene.setdefault("annotations", {})["text_regions"] = regions
    validate_scene(scene)

    for _ in range(4):
        rendered = render_visible_content_source(scene)
        by_id = {str(row["region_id"]): row for row in _records(rendered.annotation)}
        if set(by_id) != set(identifiers.values()):
            raise RuntimeError("Stress scene did not render its four token records exactly once")
        for prefix in ("positive", "negative"):
            left_id = identifiers[f"{prefix}-left"]
            right_id = identifiers[f"{prefix}-right"]
            left = _ltrb(by_id[left_id])
            right = _ltrb(by_id[right_id])
            maximum_height = max(left[3] - left[1], right[3] - right[1])
            desired_gap = gap_height_ratio * maximum_height
            desired_top = left[1] + vertical_offset_height_ratio * maximum_height
            regions[1 if prefix == "positive" else 3]["box"][0] += desired_gap - (right[0] - left[2])
            regions[1 if prefix == "positive" else 3]["box"][1] += desired_top - right[1]
        validate_scene(scene)

    rendered = render_visible_content_source(scene)
    by_id = {str(row["region_id"]): row for row in _records(rendered.annotation)}
    final_boxes = {key: _ltrb(by_id[value]) for key, value in identifiers.items()}
    for prefix in ("positive", "negative"):
        left = final_boxes[f"{prefix}-left"]
        right = final_boxes[f"{prefix}-right"]
        maximum_height = max(left[3] - left[1], right[3] - right[1])
        observed_gap = right[0] - left[2]
        observed_offset = (right[1] - left[1]) / maximum_height
        if abs(observed_gap / maximum_height - gap_height_ratio) > 0.11:
            raise RuntimeError("Rendered stress word gap left its requested band")
        if abs(observed_offset - vertical_offset_height_ratio) > 0.11:
            raise RuntimeError("Rendered stress vertical offset left its requested band")

    positive_box = _union((final_boxes["positive-left"], final_boxes["positive-right"]))
    positive_truth_id = _stable_id("truth", source_index, "positive")
    negative_truth_ids = (
        _stable_id("truth", source_index, "negative-left"),
        _stable_id("truth", source_index, "negative-right"),
    )
    truth = {
        "positive_truth": {
            "truth_id": positive_truth_id,
            "box_ltrb": list(positive_box),
            "token_boxes_ltrb": [
                list(final_boxes["positive-left"]),
                list(final_boxes["positive-right"]),
            ],
            "role": role,
            "text": " ".join(positive_tokens),
            "token_region_ids": [identifiers["positive-left"], identifiers["positive-right"]],
        },
        "negative_truths": [
            {
                "truth_id": negative_truth_ids[0],
                "box_ltrb": list(final_boxes["negative-left"]),
                "role": role,
                "text": negative_tokens[0],
                "token_region_ids": [identifiers["negative-left"]],
            },
            {
                "truth_id": negative_truth_ids[1],
                "box_ltrb": list(final_boxes["negative-right"]),
                "role": role,
                "text": negative_tokens[1],
                "token_region_ids": [identifiers["negative-right"]],
            },
        ],
        "pair_geometry_request": {
            "gap_height_ratio": gap_height_ratio,
            "vertical_offset_height_ratio": vertical_offset_height_ratio,
        },
    }
    return scene, rendered, truth


def _authenticate_parent_candidate(path: Path, expected_sha256: str) -> dict[str, Any]:
    _verify(OUTCOME_PATH, EXPECTED_OUTCOME_SHA256, "closed V44 outcome")
    outcome = _read(OUTCOME_PATH)
    if outcome.get("status") != "failed_dev_unconsumed":
        raise RuntimeError("Closed V44 outcome status changed")
    expected = outcome["evidence"]["candidate"]
    if (_repository_path(path) != expected["path"] or expected_sha256 != expected["sha256"]
            or expected_sha256 != EXPECTED_PARENT_CANDIDATE_SHA256):
        raise RuntimeError("Stress diagnostic must reuse the closed V44 candidate")
    _verify(path, expected_sha256, "V44 parent candidate")
    candidate = _read(path)
    if (candidate["detector"]["model_sha256"] != EXPECTED_DETECTOR_SHA256
            or candidate["recognizer"]["model_sha256"] != EXPECTED_RECOGNIZER_SHA256):
        raise RuntimeError("V44 model identities changed")
    return candidate


def _diagnostic_candidate(
    parent: dict[str, Any],
    binary_root: Path,
) -> tuple[dict[str, Any], dict[str, str]]:
    candidate = deepcopy(parent)
    changes = []
    for row in candidate["execution_assemblies"]:
        assembly_path = _inside_root(row["path"])
        expected_path = (binary_root / f'{row["name"]}.dll').resolve()
        if assembly_path != expected_path:
            raise RuntimeError(f"Candidate assembly path differs from binary root: {row['name']}")
        observed = _sha(assembly_path)
        if row["name"] == TOOL_ASSEMBLY:
            changes.append({"name": row["name"], "old_sha256": row["sha256"], "new_sha256": observed})
            row["sha256"] = observed
        elif row["sha256"] != observed:
            raise RuntimeError(f"Non-tool V44 execution assembly changed: {row['name']}")
    if len(changes) != 1:
        raise RuntimeError("Diagnostic candidate must refresh exactly the tool assembly binding")
    if any(
        key != "execution_assemblies" and candidate[key] != parent[key]
        for key in candidate
    ):
        raise RuntimeError("Diagnostic candidate copy changed a non-assembly field")
    return candidate, changes[0]


def prepare(arguments: argparse.Namespace) -> None:
    output = _inside_artifacts(arguments.output)
    if output.exists():
        raise RuntimeError("Use a new stress diagnostic output directory")
    if arguments.seed_count != len(GAP_CONFIGURATIONS) or arguments.seed_count * 2 > 36:
        raise RuntimeError("V1 requires exactly eight seeds and remains bounded to sixteen images")
    parent_path = _inside_root(arguments.parent_candidate)
    parent = _authenticate_parent_candidate(parent_path, arguments.parent_candidate_sha256)
    binary_root = _inside_root(arguments.binary_root)
    diagnostic_candidate, assembly_rebinding = _diagnostic_candidate(parent, binary_root)

    sources = []
    truths = []
    for seed_offset, (gap_ratio, vertical_offset) in enumerate(GAP_CONFIGURATIONS):
        dataset_seed = arguments.seed_start + seed_offset
        scenes = _build_scenes(PRESETS["smoke"], dataset_seed, require_complete_style_catalog=True)
        selected = [scene for scene in scenes if _scene_split(scene) == "train"][:2]
        if len(selected) != 2:
            raise RuntimeError("Smoke generator did not retain two leading train family scenes")
        for family_index, base_scene in enumerate(selected):
            source_index = seed_offset * 2 + family_index
            role, positive_tokens, negative_tokens = ROLE_CASES[seed_offset % len(ROLE_CASES)]
            scene, rendered, truth = _render_stress_scene(
                base_scene,
                source_index,
                role,
                positive_tokens,
                negative_tokens,
                gap_ratio,
                vertical_offset,
            )
            image_payload = png_bytes(rendered.image.convert("RGB"))
            image_sha = _sha_bytes(image_payload)
            stem = f"stress-{source_index:02d}-{image_sha[:12]}"
            image_path = output / "sources" / f"{stem}.png"
            scene_path = output / "sources" / f"{stem}.scene.json"
            annotation_path = output / "sources" / f"{stem}.annotation.json"
            image_digest = _write_new(image_path, image_payload)
            scene_digest = _write_new(scene_path, canonical_json_bytes(scene))
            annotation_digest = _write_new(annotation_path, canonical_json_bytes(rendered.annotation))
            family = _family(scene)
            font = _font_provenance(rendered.annotation)
            source = {
                "source_index": source_index,
                "dataset_seed": dataset_seed,
                "scene_seed": int(scene["seed"]),
                "family": family,
                "split": "train",
                "width": rendered.image.width,
                "height": rendered.image.height,
                "path": _repository_path(image_path),
                "sha256": image_digest,
                "scene": {"path": _repository_path(scene_path), "sha256": scene_digest},
                "annotation": {"path": _repository_path(annotation_path), "sha256": annotation_digest},
                "font": font,
                "role": role,
                "gap_height_ratio": gap_ratio,
                "vertical_offset_height_ratio": vertical_offset,
            }
            sources.append(source)
            truths.append({"source_sha256": image_sha, "source_index": source_index, **truth})

    if (len(sources) != arguments.seed_count * 2
            or len({row["path"] for row in sources}) != len(sources)
            or len({row["sha256"] for row in sources}) != len(sources)
            or len({row["source_index"] for row in sources}) != len(sources)):
        raise RuntimeError("Stress source inventory is incomplete or contains duplicates")

    candidate_path = output / "diagnostic-candidate.json"
    candidate_sha = _write_new(candidate_path, canonical_json_bytes(diagnostic_candidate))
    request = {
        "schema": f"{SCHEMA_PREFIX}.capture-request.v1",
        "scope": "project-owned-synthetic-train-only-word-fragment-stress",
        "split": "train",
        "synthetic_only": True,
        "private_data": False,
        "sealed_data": False,
        "model_inference": True,
        "production_approved": False,
        "truth_included": False,
        "candidate": {"path": _repository_path(candidate_path), "sha256": candidate_sha},
        "sources": [{"path": row["path"], "sha256": row["sha256"]} for row in sources],
    }
    request_path = output / "capture-request.json"
    request_sha = _write_new(request_path, canonical_json_bytes(request))
    truth_document = {
        "schema": f"{SCHEMA_PREFIX}.truth.v1",
        "scope": "project-owned-synthetic-train-only-word-fragment-stress",
        "split": "train",
        "source_count": len(sources),
        "positive_logical_truth_count": len(sources),
        "negative_neighbor_pair_count": len(sources),
        "truths": truths,
    }
    truth_path = output / "truth.json"
    truth_sha = _write_new(truth_path, canonical_json_bytes(truth_document))
    preflight = {
        "schema": f"{SCHEMA_PREFIX}.preflight.v1",
        "status": "train_only_sources_prepared",
        "scope": "project-owned synthetic train-only word-fragment geometry diagnostic",
        "source_count": len(sources),
        "maximum_source_count": 36,
        "dataset_seed_start": arguments.seed_start,
        "dataset_seed_count": arguments.seed_count,
        "families": sorted({row["family"] for row in sources}),
        "gap_height_ratios": sorted({row["gap_height_ratio"] for row in sources}),
        "vertical_offset_height_ratios": sorted({row["vertical_offset_height_ratio"] for row in sources}),
        "sources": sources,
        "truth": {"path": _repository_path(truth_path), "sha256": truth_sha},
        "capture_request": {"path": _repository_path(request_path), "sha256": request_sha},
        "diagnostic_candidate": {"path": _repository_path(candidate_path), "sha256": candidate_sha},
        "parent_candidate": {"path": _repository_path(parent_path), "sha256": arguments.parent_candidate_sha256},
        "diagnostic_candidate_rebinding": {
            "only_changed_field": "execution_assemblies.GraphReader.SyntheticRuntimeEvidence.sha256",
            "assembly": assembly_rebinding,
            "model_native_license_and_product_assemblies_unchanged": True,
        },
        "generator_sources": [
            {"path": relative, "sha256": _sha(ROOT / relative)} for relative in GENERATOR_SOURCES
        ],
        "truth_provenance": (
            "Positive labels are two rendered token runs grouped into one local logical truth; "
            "negative neighbors are two rendered token runs with distinct local truths. Runtime input "
            "contains full source pixels and hashes only."
        ),
        "geometry_design": (
            "Positive and negative pairs intentionally share the same requested gap and vertical-offset "
            "bands. This controlled ambiguity measures whether detector output geometry itself separates "
            "the two local truth structures; it is not a natural-distribution estimate or proof that a "
            "general geometry rule is impossible."
        ),
        "font_dependencies": sorted(
            {
                (row["font"]["resolved_path"], row["font"]["sha256"]): row["font"]
                for row in sources
            }.values(),
            key=lambda row: (str(row["resolved_file"]).casefold(), str(row["sha256"])),
        ),
        "runtime_request_contains_truth_text_roles_or_crops": False,
        "detector_operating_thresholds_changed": False,
        "optimizer_steps": 0,
        "private_reads": 0,
        "sealed_reads": 0,
        "new_model_revision": False,
    }
    preflight_path = output / "preflight.json"
    preflight_sha = _write_new(preflight_path, canonical_json_bytes(preflight))
    print(json.dumps({
        "preflight": _repository_path(preflight_path),
        "preflight_sha256": preflight_sha,
        "capture_request": request["schema"],
        "capture_request_sha256": request_sha,
        "diagnostic_candidate_sha256": candidate_sha,
        "source_count": len(sources),
    }, sort_keys=True))


def _box(value: Sequence[Any]) -> tuple[float, float, float, float]:
    if len(value) != 4:
        raise RuntimeError("Box must have four values")
    box = tuple(float(item) for item in value)
    if not all(math.isfinite(item) for item in box) or box[2] <= box[0] or box[3] <= box[1]:
        raise RuntimeError("Box is invalid")
    return box


def _iou(left: Sequence[float], right: Sequence[float]) -> float:
    width = max(0.0, min(left[2], right[2]) - max(left[0], right[0]))
    height = max(0.0, min(left[3], right[3]) - max(left[1], right[1]))
    intersection = width * height
    left_area = (left[2] - left[0]) * (left[3] - left[1])
    right_area = (right[2] - right[0]) * (right[3] - right[1])
    return intersection / max(1e-12, left_area + right_area - intersection)


def _pair_geometry(left: Sequence[float], right: Sequence[float]) -> dict[str, float]:
    left_height = left[3] - left[1]
    right_height = right[3] - right[1]
    minimum_height = min(left_height, right_height)
    maximum_height = max(left_height, right_height)
    gap = left[0] - right[2] if left[0] > right[2] else right[0] - left[2] if right[0] > left[2] else 0.0
    overlap = max(0.0, min(left[3], right[3]) - max(left[1], right[1]))
    union = _union((left, right))
    return {
        "horizontal_gap_pixels": gap,
        "horizontal_gap_height_ratio": gap / maximum_height,
        "vertical_overlap_ratio": overlap / minimum_height,
        "height_ratio": maximum_height / minimum_height,
        "merged_height_growth_ratio": (union[3] - union[1]) / maximum_height,
    }


def _best_assigned_pair(
    raw: Sequence[Sequence[float]],
    token_truths: Sequence[Sequence[float]],
) -> tuple[int, int] | None:
    if len(token_truths) != 2:
        raise RuntimeError("Assigned pair must have exactly two token truths")
    best = None
    for left_index, left in enumerate(raw):
        left_iou = _iou(left, token_truths[0])
        if left_iou <= 0:
            continue
        for right_index, right in enumerate(raw):
            if left_index == right_index:
                continue
            right_iou = _iou(right, token_truths[1])
            if right_iou <= 0:
                continue
            value = (left_iou + right_iou, min(left_iou, right_iou))
            if best is None or value > best[0]:
                best = (value, left_index, right_index)
    return None if best is None else (best[1], best[2])


def _missing_assigned_pair_reasons(
    raw: Sequence[Sequence[float]],
    token_truths: Sequence[Sequence[float]],
) -> tuple[str, ...]:
    overlaps = [any(_iou(box, truth) > 0 for box in raw) for truth in token_truths]
    reasons = []
    if not overlaps[0]:
        reasons.append("left_token_without_raw_overlap")
    if not overlaps[1]:
        reasons.append("right_token_without_raw_overlap")
    if all(overlaps):
        reasons.append("without_two_distinct_assigned_raw_boxes")
    return tuple(reasons)


def _percentile(values: Sequence[float], fraction: float) -> float | None:
    if not values:
        return None
    ordered = sorted(values)
    position = (len(ordered) - 1) * fraction
    lower = math.floor(position)
    upper = math.ceil(position)
    if lower == upper:
        return ordered[lower]
    return ordered[lower] * (upper - position) + ordered[upper] * (position - lower)


def _distribution(values: Iterable[float]) -> dict[str, float | int | None]:
    rows = list(values)
    return {
        "count": len(rows),
        "minimum": min(rows) if rows else None,
        "p25": _percentile(rows, 0.25),
        "median": statistics.median(rows) if rows else None,
        "p75": _percentile(rows, 0.75),
        "maximum": max(rows) if rows else None,
    }


def score(arguments: argparse.Namespace) -> None:
    preflight_path = _inside_artifacts(arguments.preflight)
    capture_path = _inside_artifacts(arguments.capture)
    output_path = _inside_artifacts(arguments.output)
    if output_path.exists():
        raise RuntimeError("Use a new stress score output")
    _verify(preflight_path, arguments.preflight_sha256, "stress preflight")
    _verify(capture_path, arguments.capture_sha256, "stress raw capture")
    preflight = _read(preflight_path)
    capture = _read(capture_path)
    truth_descriptor = preflight["truth"]
    truth_path = _inside_root(truth_descriptor["path"])
    _verify(truth_path, truth_descriptor["sha256"], "stress truth")
    truth = _read(truth_path)
    request_descriptor = preflight["capture_request"]
    request_path = _inside_artifacts(request_descriptor["path"])
    _verify(request_path, request_descriptor["sha256"], "stress capture request")
    request = _read(request_path)
    diagnostic_descriptor = preflight["diagnostic_candidate"]
    diagnostic_path = _inside_artifacts(diagnostic_descriptor["path"])
    _verify(diagnostic_path, diagnostic_descriptor["sha256"], "stress diagnostic candidate")
    diagnostic_candidate = _read(diagnostic_path)
    parent_descriptor = preflight["parent_candidate"]
    parent_path = _inside_root(parent_descriptor["path"])
    parent = _authenticate_parent_candidate(parent_path, parent_descriptor["sha256"])
    tool_rows = [
        row for row in diagnostic_candidate["execution_assemblies"]
        if row["name"] == TOOL_ASSEMBLY
    ]
    if len(tool_rows) != 1:
        raise RuntimeError("Stress diagnostic candidate tool binding is invalid")
    expected_diagnostic, assembly_rebinding = _diagnostic_candidate(
        parent, _inside_root(tool_rows[0]["path"]).parent
    )
    if (diagnostic_candidate != expected_diagnostic
            or assembly_rebinding != preflight["diagnostic_candidate_rebinding"]["assembly"]
            or assembly_rebinding["old_sha256"] == assembly_rebinding["new_sha256"]):
        raise RuntimeError("Stress diagnostic candidate changed outside the tool assembly hash")
    for row in preflight["generator_sources"]:
        _verify(_inside_root(row["path"]), row["sha256"], "stress generator source")
    input_scope = capture.get("input_scope")
    if (preflight.get("schema") != f"{SCHEMA_PREFIX}.preflight.v1"
            or preflight.get("status") != "train_only_sources_prepared"
            or truth.get("schema") != f"{SCHEMA_PREFIX}.truth.v1"
            or truth.get("scope") != "project-owned-synthetic-train-only-word-fragment-stress"
            or truth.get("split") != "train"
            or request.get("schema") != f"{SCHEMA_PREFIX}.capture-request.v1"
            or request.get("scope") != "project-owned-synthetic-train-only-word-fragment-stress"
            or request.get("split") != "train"
            or request.get("synthetic_only") is not True
            or request.get("private_data") is not False
            or request.get("sealed_data") is not False
            or request.get("model_inference") is not True
            or request.get("production_approved") is not False
            or request.get("truth_included") is not False
            or request.get("candidate") != diagnostic_descriptor
            or capture.get("schema") != f"{SCHEMA_PREFIX}.raw-capture.v1"
            or capture.get("scope") != "project-owned-synthetic-train-only-word-fragment-stress"
            or capture.get("status") != "completed"
            or capture.get("split") != "train"
            or capture.get("model_inference") is not True
            or capture.get("production_approved") is not False
            or input_scope != {
                "source": "authenticated_capture_request",
                "synthetic_only_declared": True,
                "private_data_declared": False,
                "sealed_data_declared": False,
                "independently_verified": False,
            }
            or capture.get("truth_received_by_runtime") is not False
            or capture.get("truth_based_crops") is not False
            or capture.get("recognition_text_serialized") is not False
            or capture.get("optimizer_steps") != 0
            or capture.get("new_model_revision") is not False
            or capture.get("request") != request_descriptor
            or capture.get("candidate") != diagnostic_descriptor
            or capture.get("request_sha256") != preflight["capture_request"]["sha256"]
            or capture.get("candidate_sha256") != preflight["diagnostic_candidate"]["sha256"]):
        raise RuntimeError("Stress preparation, capture, and truth bindings disagree")
    expected_rows = preflight["sources"]
    captured_rows = capture["sources"]
    truth_rows = truth["truths"]
    expected_sources = {row["sha256"]: row for row in expected_rows}
    captured_sources = {row["source_sha256"]: row for row in captured_rows}
    truth_sources = {row["source_sha256"]: row for row in truth_rows}
    if (len(expected_rows) != preflight["source_count"]
            or preflight["source_count"] > preflight["maximum_source_count"]
            or len(captured_rows) != capture["source_count"]
            or len(truth_rows) != truth["source_count"]
            or truth["positive_logical_truth_count"] != truth["source_count"]
            or truth["negative_neighbor_pair_count"] != truth["source_count"]
            or len(expected_sources) != len(expected_rows)
            or len(captured_sources) != len(captured_rows)
            or len(truth_sources) != len(truth_rows)):
        raise RuntimeError("Stress source inventory count or uniqueness changed")
    if set(expected_sources) != set(captured_sources) or set(expected_sources) != set(truth_sources):
        raise RuntimeError("Stress source inventory changed")
    if request["sources"] != [
        {"path": row["path"], "sha256": row["sha256"]} for row in expected_rows
    ]:
        raise RuntimeError("Stress runtime request source order or identity changed")
    for source in expected_rows:
        _verify(_inside_artifacts(source["path"]), source["sha256"], "stress source image")
        scene = source["scene"]
        annotation = source["annotation"]
        _verify(_inside_artifacts(scene["path"]), scene["sha256"], "stress source scene")
        annotation_path = _inside_artifacts(annotation["path"])
        _verify(annotation_path, annotation["sha256"], "stress source annotation")
        if _font_provenance(_read(annotation_path)) != source["font"]:
            raise RuntimeError("Stress source font provenance changed")

    positive_rows = []
    negative_rows = []
    missing = Counter()
    for source_sha in sorted(expected_sources):
        source = expected_sources[source_sha]
        truth_row = truth_sources[source_sha]
        raw = tuple(_box(row) for row in captured_sources[source_sha]["raw_boxes_ltrb"])
        positive_truth = _box(truth_row["positive_truth"]["box_ltrb"])
        positive_token_truths = tuple(
            _box(row) for row in truth_row["positive_truth"]["token_boxes_ltrb"]
        )
        positive_pair = _best_assigned_pair(raw, positive_token_truths)
        if positive_pair is None:
            for reason in _missing_assigned_pair_reasons(raw, positive_token_truths):
                missing["positive_" + reason] += 1
        else:
            geometry = _pair_geometry(raw[positive_pair[0]], raw[positive_pair[1]])
            left_token_iou = _iou(raw[positive_pair[0]], positive_token_truths[0])
            right_token_iou = _iou(raw[positive_pair[1]], positive_token_truths[1])
            geometry.update({
                "role": source["role"],
                "family": source["family"],
                "requested_gap_height_ratio": source["gap_height_ratio"],
                "requested_vertical_offset_height_ratio": source["vertical_offset_height_ratio"],
                "left_assigned_token_iou": left_token_iou,
                "right_assigned_token_iou": right_token_iou,
                "minimum_assigned_token_iou": min(left_token_iou, right_token_iou),
                "pair_union_iou_with_logical_truth": _iou(
                    _union((raw[positive_pair[0]], raw[positive_pair[1]])), positive_truth
                ),
            })
            positive_rows.append(geometry)

        negative_truths = tuple(_box(row["box_ltrb"]) for row in truth_row["negative_truths"])
        negative_pair = _best_assigned_pair(raw, negative_truths)
        if negative_pair is None:
            for reason in _missing_assigned_pair_reasons(raw, negative_truths):
                missing["negative_" + reason] += 1
        else:
            geometry = _pair_geometry(raw[negative_pair[0]], raw[negative_pair[1]])
            left_token_iou = _iou(raw[negative_pair[0]], negative_truths[0])
            right_token_iou = _iou(raw[negative_pair[1]], negative_truths[1])
            geometry.update({
                "role": source["role"],
                "family": source["family"],
                "requested_gap_height_ratio": source["gap_height_ratio"],
                "requested_vertical_offset_height_ratio": source["vertical_offset_height_ratio"],
                "left_assigned_token_iou": left_token_iou,
                "right_assigned_token_iou": right_token_iou,
                "minimum_assigned_token_iou": min(left_token_iou, right_token_iou),
            })
            negative_rows.append(geometry)

    feature_names = (
        "horizontal_gap_pixels",
        "horizontal_gap_height_ratio",
        "vertical_overlap_ratio",
        "height_ratio",
        "merged_height_growth_ratio",
    )
    distributions = {
        feature: {
            "positive": _distribution(float(row[feature]) for row in positive_rows),
            "negative": _distribution(float(row[feature]) for row in negative_rows),
        }
        for feature in feature_names
    }
    for feature, rows in distributions.items():
        positive = rows["positive"]
        negative = rows["negative"]
        rows["range_disjoint"] = bool(
            positive["count"] and negative["count"]
            and (positive["maximum"] < negative["minimum"]
                 or negative["maximum"] < positive["minimum"])
        )
    assignment_quality = {
        feature: {
            "positive": _distribution(float(row[feature]) for row in positive_rows),
            "negative": _distribution(float(row[feature]) for row in negative_rows),
        }
        for feature in (
            "left_assigned_token_iou",
            "right_assigned_token_iou",
            "minimum_assigned_token_iou",
        )
    }
    assignment_quality["positive_pair_union_iou_with_logical_truth"] = {
        "positive": _distribution(
            float(row["pair_union_iou_with_logical_truth"]) for row in positive_rows
        ),
        "negative": None,
    }

    result = {
        "schema": f"{SCHEMA_PREFIX}.separability.v1",
        "status": "train_only_raw_pair_geometry_measured",
        "scope": "project-owned synthetic train-only raw detector geometry",
        "inputs": {
            "preflight": {"path": _repository_path(preflight_path), "sha256": arguments.preflight_sha256},
            "capture": {"path": _repository_path(capture_path), "sha256": arguments.capture_sha256},
            "truth": truth_descriptor,
            "candidate": preflight["diagnostic_candidate"],
            "parent_candidate": preflight["parent_candidate"],
        },
        "source_count": len(expected_sources),
        "positive_pair_count": len(positive_rows),
        "negative_pair_count": len(negative_rows),
        "missing_pair_counts": dict(sorted(missing.items())),
        "by_role": {
            role: {
                "positive_pairs": sum(row["role"] == role for row in positive_rows),
                "negative_pairs": sum(row["role"] == role for row in negative_rows),
            }
            for role in sorted({row["role"] for row in preflight["sources"]})
        },
        "by_family": {
            family: {
                "positive_pairs": sum(row["family"] == family for row in positive_rows),
                "negative_pairs": sum(row["family"] == family for row in negative_rows),
            }
            for family in sorted({row["family"] for row in preflight["sources"]})
        },
        "feature_distributions": distributions,
        "assignment_quality_distributions": assignment_quality,
        "pair_assignment": (
            "Each measured pair contains two distinct raw boxes assigned one-to-one to the two "
            "rendered token boxes, with positive IoU required for each assignment."
        ),
        "geometry_design": preflight["geometry_design"],
        "input_scope_basis": capture["input_scope"],
        "separability_interpretation": (
            "Range overlap is descriptive train-only evidence. This scorer does not select a joining "
            "rule, alter detector operating thresholds, or evaluate recognition text or roles. The "
            "controlled same-band construction is not an estimate of natural label frequencies."
        ),
        "recognition_text_scored": False,
        "runtime_truth_received": False,
        "truth_based_crops": False,
        "detector_operating_thresholds_changed": False,
        "optimizer_steps": 0,
        "private_reads": 0,
        "sealed_reads": 0,
        "new_model_revision": False,
        "production_approval": False,
    }
    _write_new(output_path, canonical_json_bytes(result))
    print(json.dumps({
        "output": _repository_path(output_path),
        "sha256": _sha(output_path),
        "positive_pairs": len(positive_rows),
        "negative_pairs": len(negative_rows),
    }, sort_keys=True))


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    prepare_parser = subparsers.add_parser("prepare")
    prepare_parser.add_argument("--output", required=True)
    prepare_parser.add_argument("--parent-candidate", required=True)
    prepare_parser.add_argument("--parent-candidate-sha256", required=True)
    prepare_parser.add_argument("--binary-root", required=True)
    prepare_parser.add_argument("--seed-start", type=int, default=410)
    prepare_parser.add_argument("--seed-count", type=int, default=len(GAP_CONFIGURATIONS))
    prepare_parser.set_defaults(handler=prepare)
    score_parser = subparsers.add_parser("score")
    score_parser.add_argument("--preflight", required=True)
    score_parser.add_argument("--preflight-sha256", required=True)
    score_parser.add_argument("--capture", required=True)
    score_parser.add_argument("--capture-sha256", required=True)
    score_parser.add_argument("--output", required=True)
    score_parser.set_defaults(handler=score)
    return parser


def main(argv: Sequence[str] | None = None) -> None:
    arguments = build_parser().parse_args(argv)
    arguments.handler(arguments)


if __name__ == "__main__":
    main()
