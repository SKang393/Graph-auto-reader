# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Score authenticated DB initial/expanded geometry observations on train/dev.

This is a diagnostic-only evaluator. It accepts the fixed project-owned
synthetic protocol, verifies the annotation-free runtime exchange and every
saved observation before regenerating truth, and compares the two polygons
from each identical accepted DB contour with the established IoU 0.5 matcher.
"""

from __future__ import annotations

import argparse
from hashlib import sha256
import json
import math
from pathlib import Path
import sys
import time
from typing import Any, Mapping, Sequence

REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
if str(REPOSITORY_ROOT) not in sys.path:
    sys.path.insert(0, str(REPOSITORY_ROOT))

import score_family_ocr as family_ocr
from ml.ocr.component_region_detector_v6.dataset import Box, Component
from ml.ocr.real_range_proposal_v34.pipeline import maximum_cardinality_matches


PROTOCOL_PATH = Path("ml/ocr/official_bakeoff/db_geometry_diagnostic_protocol.json")
EXPECTED_PROTOCOL_SHA256 = "58bd372a12384c8a86a9f9aa6a5481e799870f73282f0f33c6a512838a7347f0"
PROTOCOL_KEYS = {
    "evidence_policy", "hypothesis", "isolated_change", "split_identities",
    "metric", "acceptance_bar", "budget",
}
IDENTITY_KEYS = {"train_manifest", "dev_manifest", "runtime_candidate"}
BOUND_FILE_KEYS = {"path", "sha256"}
OUTPUT_SCHEMA = "graphreader.synthetic-db-geometry-score.v1"
SIDECAR_SCHEMA = "graphreader.synthetic-db-geometry-observation.v1"
SIDECAR_SCOPE = "local-synthetic-train-dev-diagnostic"
EXPECTED_TRUTH_COUNTS = {"train": 146, "validation": 183}
EXPECTED_TOTAL_TRUTHS = 329
EXPECTED_INVOCATIONS = (("axis-masked", "masked"), ("unmasked", "unmasked"))
MATCH_IOU_MINIMUM = 0.5

EvidenceError = family_ocr.EvidenceError


def _require_mapping(value: Any, label: str) -> Mapping[str, Any]:
    if not isinstance(value, dict):
        raise EvidenceError(f"{label} must be an object")
    return value


def _require_list(value: Any, label: str) -> list[Any]:
    if not isinstance(value, list):
        raise EvidenceError(f"{label} must be an array")
    return value


def _repository_file(path_text: Any, label: str) -> Path:
    if not isinstance(path_text, str):
        raise EvidenceError(f"{label} path must be a string")
    relative = Path(path_text)
    if relative.is_absolute():
        raise EvidenceError(f"{label} path must be repository-relative")
    path = (REPOSITORY_ROOT / relative).resolve()
    root = REPOSITORY_ROOT.resolve()
    if path == root or root not in path.parents:
        raise EvidenceError(f"{label} path escaped the repository")
    return path


def _external_file(path_text: Any, expected_sha256: Any, label: str) -> Path:
    if not isinstance(path_text, str) or not path_text.strip():
        raise EvidenceError(f"{label} path must be a nonempty string")
    path = Path(path_text).resolve()
    expected = family_ocr._require_sha256(expected_sha256, f"{label} SHA-256")
    if not path.is_file() or family_ocr._sha256_file(path) != expected:
        raise EvidenceError(f"{label} bytes do not match the candidate")
    return path


def _validate_bound_file(record: Any, label: str) -> tuple[Path, str]:
    value = _require_mapping(record, label)
    family_ocr._require_exact_keys(value, BOUND_FILE_KEYS, label)
    path = _repository_file(value["path"], label)
    expected = family_ocr._require_sha256(value["sha256"], f"{label} SHA-256")
    if not path.is_file() or family_ocr._sha256_file(path) != expected:
        raise EvidenceError(f"{label} bytes differ from the reviewed protocol")
    return path, expected


def _load_protocol(protocol_path: Path) -> tuple[dict[str, Any], bytes, dict[str, tuple[Path, str]]]:
    expected_path = (REPOSITORY_ROOT / PROTOCOL_PATH).resolve()
    if protocol_path.resolve() != expected_path:
        raise EvidenceError("DB geometry scoring requires the reviewed repository protocol path")
    protocol, payload = family_ocr._load_object(expected_path, "DB geometry protocol")
    if family_ocr._sha256_bytes(payload) != EXPECTED_PROTOCOL_SHA256:
        raise EvidenceError("DB geometry protocol bytes differ from the reviewed protocol")
    family_ocr._require_exact_keys(protocol, PROTOCOL_KEYS, "DB geometry protocol")
    if (
        protocol["evidence_policy"] != "ml/policy/evidence-policy.json"
        or not isinstance(protocol["hypothesis"], str) or not protocol["hypothesis"].strip()
        or not isinstance(protocol["isolated_change"], str) or not protocol["isolated_change"].strip()
        or not isinstance(protocol["metric"], str) or not protocol["metric"].strip()
        or not isinstance(protocol["acceptance_bar"], str) or not protocol["acceptance_bar"].strip()
    ):
        raise EvidenceError("DB geometry protocol metadata is invalid")
    budget = _require_mapping(protocol["budget"], "DB geometry protocol budget")
    family_ocr._require_exact_keys(budget, {"train_dev_runs", "sealed_runs"}, "DB geometry protocol budget")
    if budget["train_dev_runs"] != "unlimited" or budget["sealed_runs"] != 0:
        raise EvidenceError("DB geometry protocol cannot authorize sealed evidence")
    identities = _require_mapping(protocol["split_identities"], "DB geometry split identities")
    family_ocr._require_exact_keys(identities, IDENTITY_KEYS, "DB geometry split identities")
    bound = {
        key: _validate_bound_file(identities[key], f"DB geometry {key}")
        for key in sorted(IDENTITY_KEYS)
    }
    return protocol, payload, bound


def _validate_candidate(candidate_path: Path, expected_hash: str) -> dict[str, Any]:
    candidate, payload = family_ocr._load_object(candidate_path, "runtime candidate")
    if family_ocr._sha256_bytes(payload) != expected_hash:
        raise EvidenceError("runtime candidate bytes differ from the reviewed protocol")
    if candidate.get("schema") != "graphreader.local-synthetic-ocr-candidate.v1":
        raise EvidenceError("runtime candidate schema is foreign")
    if candidate.get("production_approved") is not False:
        raise EvidenceError("runtime candidate must remain unapproved")
    native_sha = family_ocr._require_sha256(candidate.get("native_sha256"), "candidate native SHA-256")
    native_scope = candidate.get("native_scope")
    if not isinstance(native_scope, str) or not native_scope.strip():
        raise EvidenceError("candidate native scope is missing")
    _external_file(candidate.get("native_path"), native_sha, "candidate native runtime")

    detector = _require_mapping(candidate.get("detector"), "candidate detector")
    required = {"model_path", "model_id", "model_version", "model_sha256", "manifest_path", "manifest_sha256"}
    if not required.issubset(detector):
        raise EvidenceError("candidate detector descriptor is incomplete")
    model_sha = family_ocr._require_sha256(detector["model_sha256"], "detector model SHA-256")
    manifest_sha = family_ocr._require_sha256(detector["manifest_sha256"], "detector manifest SHA-256")
    _external_file(detector["model_path"], model_sha, "detector model")
    _external_file(detector["manifest_path"], manifest_sha, "detector manifest")
    for field in ("model_id", "model_version"):
        if not isinstance(detector[field], str) or not detector[field].strip():
            raise EvidenceError(f"candidate detector {field} is missing")
    return {
        "sha256": expected_hash,
        "native_sha256": native_sha,
        "native_scope": native_scope,
        "detector": {
            "model_id": detector["model_id"],
            "model_version": detector["model_version"],
            "model_sha256": model_sha,
            "manifest_path": detector["manifest_path"],
            "manifest_sha256": manifest_sha,
        },
    }


def _runtime_assemblies(value: Any, label: str) -> tuple[tuple[str, str], ...]:
    raw = _require_list(value, label)
    if not raw:
        raise EvidenceError(f"{label} must not be empty")
    result: list[tuple[str, str]] = []
    names: set[str] = set()
    for index, item in enumerate(raw):
        record = _require_mapping(item, f"{label} {index}")
        family_ocr._require_exact_keys(record, {"name", "sha256"}, f"{label} {index}")
        name = record["name"]
        if not isinstance(name, str) or not name.strip() or name.casefold() in names:
            raise EvidenceError(f"{label} names must be nonempty and unique")
        names.add(name.casefold())
        result.append((name, family_ocr._require_sha256(record["sha256"], f"{label} {name} SHA-256")))
    return tuple(result)


def _validate_execution_manifest(
    manifest_path: Path,
    expected_sha256: str,
) -> tuple[bytes, tuple[tuple[str, str], ...]]:
    manifest_path = family_ocr._require_artifact_path(
        manifest_path, REPOSITORY_ROOT, "execution manifest")
    payload = manifest_path.read_bytes()
    expected = family_ocr._require_sha256(expected_sha256, "execution manifest SHA-256")
    if family_ocr._sha256_bytes(payload) != expected:
        raise EvidenceError("execution manifest bytes differ from the caller-bound SHA-256")
    try:
        raw_entries = json.loads(payload)
    except json.JSONDecodeError as exception:
        raise EvidenceError(f"execution manifest is invalid JSON: {exception}") from exception
    entries = _require_list(raw_entries, "execution manifest")
    if not entries:
        raise EvidenceError("execution manifest must not be empty")
    files: dict[str, str] = {}
    for index, raw_entry in enumerate(entries):
        entry = _require_mapping(raw_entry, f"execution manifest entry {index}")
        family_ocr._require_exact_keys(
            entry, {"path", "sha256", "bytes"}, f"execution manifest entry {index}")
        name = entry["path"]
        if not isinstance(name, str) or Path(name).name != name or name.casefold() in files:
            raise EvidenceError("execution manifest paths must be unique local basenames")
        expected_file_sha = family_ocr._require_sha256(
            entry["sha256"], f"execution manifest {name} SHA-256")
        expected_size = family_ocr._require_int(
            entry["bytes"], f"execution manifest {name} byte count", minimum=1)
        path = family_ocr._require_owned_file(
            manifest_path.parent, name, f"execution file {name}", repository_root=REPOSITORY_ROOT)
        if (
            not path.is_file()
            or path.stat().st_size != expected_size
            or family_ocr._sha256_file(path) != expected_file_sha
        ):
            raise EvidenceError(f"execution file bytes differ from the manifest: {name}")
        files[name.casefold()] = expected_file_sha
    required_names = (
        "GraphReader.SyntheticRuntimeEvidence", "GraphReader.App", "GraphReader.Axis",
        "GraphReader.Ocr", "GraphReader.Inference", "GraphReader.Pdf",
    )
    assemblies: list[tuple[str, str]] = []
    for name in required_names:
        file_sha = files.get(f"{name}.dll".casefold())
        if file_sha is None:
            raise EvidenceError(f"execution manifest is missing the reported assembly file {name}.dll")
        assemblies.append((name, file_sha))
    return payload, tuple(assemblies)


def _same_number(left: Any, right: Any, label: str) -> float:
    actual = family_ocr._require_number(left, label)
    expected = family_ocr._require_number(right, f"expected {label}")
    if actual != expected:
        raise EvidenceError(f"{label} differs from the runtime report")
    return actual


def _rect_tuple(value: Any, label: str) -> tuple[float, float, float, float]:
    record = _require_mapping(value, label)
    result = tuple(family_ocr._require_number(record.get(field), f"{label} {field}") for field in ("x", "y", "width", "height"))
    if result[2] <= 0 or result[3] <= 0:
        raise EvidenceError(f"{label} must have positive dimensions")
    return result  # type: ignore[return-value]


def _matrix_tuple(value: Any, label: str) -> tuple[float, ...]:
    raw = _require_list(value, label)
    if len(raw) != 9:
        raise EvidenceError(f"{label} must contain nine values")
    return tuple(family_ocr._require_number(item, f"{label} value") for item in raw)


def _validate_point(value: Any, label: str, width: int, height: int) -> tuple[float, float]:
    record = _require_mapping(value, label)
    x = family_ocr._require_number(record.get("x"), f"{label} x")
    y = family_ocr._require_number(record.get("y"), f"{label} y")
    if record.get("is_finite") is not True or x < 0 or y < 0 or x > width or y > height:
        raise EvidenceError(f"{label} must be finite and inside the panel")
    return x, y


def _polygon_points(value: Any, label: str, width: int, height: int) -> tuple[tuple[float, float], ...]:
    polygon = _require_mapping(value, label)
    raw_points = _require_list(polygon.get("points"), f"{label} points")
    if len(raw_points) != 4:
        raise EvidenceError(f"{label} must contain exactly four recorded points")
    points = tuple(_validate_point(point, f"{label} point {index}", width, height)
                   for index, point in enumerate(raw_points))
    left = min(point[0] for point in points)
    top = min(point[1] for point in points)
    right = max(point[0] for point in points)
    bottom = max(point[1] for point in points)
    if right <= left or bottom <= top:
        raise EvidenceError(f"{label} has nonpositive bounds")
    bounds = _require_mapping(polygon.get("bounds"), f"{label} bounds")
    expected = {
        "x": left, "y": top, "width": right - left, "height": bottom - top,
        "left": left, "top": top, "right": right, "bottom": bottom,
    }
    for field, expected_value in expected.items():
        if family_ocr._require_number(bounds.get(field), f"{label} bounds {field}") != expected_value:
            raise EvidenceError(f"{label} serialized bounds differ from its recorded points")
    if bounds.get("is_valid") is not True:
        raise EvidenceError(f"{label} serialized bounds are invalid")
    return points


def _component(points: Sequence[tuple[float, float]], offset_x: int, offset_y: int) -> Component:
    left = min(point[0] for point in points) + offset_x
    top = min(point[1] for point in points) + offset_y
    right = max(point[0] for point in points) + offset_x
    bottom = max(point[1] for point in points) + offset_y
    return Component(left, top, right - 1.0, bottom - 1.0, (right - left) * (bottom - top), 1)


def _validate_report_model_region(
    contour: Mapping[str, Any],
    region: Any,
    label: str,
    width: int,
    height: int,
) -> tuple[tuple[float, float], ...]:
    model_region = _require_mapping(region, f"{label} returned region")
    identifier = contour.get("returned_region_id")
    if not isinstance(identifier, str) or not identifier or model_region.get("region_id") != identifier:
        raise EvidenceError(f"{label} returned-region identity differs from the runtime report")
    expanded = _polygon_points(contour.get("expanded_polygon"), f"{label} expanded polygon", width, height)
    report_polygon = _require_mapping(model_region.get("polygon"), f"{label} returned polygon")
    report_points_raw = _require_list(report_polygon.get("points"), f"{label} returned polygon points")
    report_points = tuple(_validate_point(point, f"{label} returned point {index}", width, height)
                          for index, point in enumerate(report_points_raw))
    if expanded != report_points:
        raise EvidenceError(f"{label} expanded polygon points differ from ModelRegions")
    _same_number(contour.get("detection_confidence"), model_region.get("detection_confidence"),
                 f"{label} detection confidence")
    evidence = _require_mapping(model_region.get("evidence"), f"{label} returned evidence")
    _same_number(contour.get("ink_density"), evidence.get("ink_density"), f"{label} ink density")
    return expanded


def _validate_sidecar(
    panel: Mapping[str, Any],
    source: Mapping[str, Any],
    report: Mapping[str, Any],
    report_path: Path,
    manifest_sha256: str,
    protocol_sha256: str,
    candidate: Mapping[str, Any],
    assemblies: tuple[tuple[str, str], ...],
) -> dict[str, dict[str, tuple[Component, ...]]]:
    diagnostic = _require_mapping(panel.get("ocr_proposal_diagnostic"), "OCR proposal diagnostic")
    if diagnostic.get("coordinate_space") != "original_pixels" or diagnostic.get("used_as_accepted_evidence") is not False:
        raise EvidenceError("OCR proposal diagnostic coordinate space or evidence use is invalid")
    descriptor = _require_mapping(diagnostic.get("db_geometry_diagnostic_sidecar"), "DB geometry sidecar descriptor")
    family_ocr._require_exact_keys(descriptor, {"file", "sha256", "byte_count"}, "DB geometry sidecar descriptor")
    panel_directory = report_path.parent / str(source["image_sha256"]) / str(panel["panel_id"])
    sidecar_path = family_ocr._require_owned_file(
        panel_directory, descriptor["file"], "DB geometry sidecar", repository_root=REPOSITORY_ROOT)
    payload = sidecar_path.read_bytes()
    if (
        family_ocr._sha256_bytes(payload) != family_ocr._require_sha256(descriptor["sha256"], "DB geometry sidecar SHA-256")
        or len(payload) != family_ocr._require_int(descriptor["byte_count"], "DB geometry sidecar byte count", minimum=1)
    ):
        raise EvidenceError("DB geometry sidecar bytes differ from its report descriptor")
    try:
        sidecar = json.loads(payload)
    except json.JSONDecodeError as exception:
        raise EvidenceError(f"DB geometry sidecar is invalid JSON: {exception}") from exception
    sidecar = _require_mapping(sidecar, "DB geometry sidecar")
    if (
        sidecar.get("schema") != SIDECAR_SCHEMA
        or sidecar.get("scope") != SIDECAR_SCOPE
        or sidecar.get("production_approved") is not False
        or sidecar.get("training_input_ready") is not False
        or sidecar.get("truth_used_by_runtime") is not False
    ):
        raise EvidenceError("DB geometry sidecar schema, scope, or scientific flags are invalid")
    protocol = _require_mapping(sidecar.get("protocol"), "DB geometry sidecar protocol")
    if protocol.get("path") != PROTOCOL_PATH.as_posix() or family_ocr._require_sha256(
            protocol.get("sha256"), "sidecar protocol SHA-256") != protocol_sha256:
        raise EvidenceError("DB geometry sidecar does not bind the reviewed protocol")
    if family_ocr._require_sha256(sidecar.get("input_manifest_sha256"), "sidecar manifest SHA-256") != manifest_sha256:
        raise EvidenceError("DB geometry sidecar does not bind the input manifest")
    if family_ocr._require_sha256(sidecar.get("candidate_sha256"), "sidecar candidate SHA-256") != candidate["sha256"]:
        raise EvidenceError("DB geometry sidecar does not bind the runtime candidate")
    side_source = _require_mapping(sidecar.get("source"), "DB geometry sidecar source")
    if side_source != {"image_sha256": source["image_sha256"], "width": source["width"], "height": source["height"]}:
        raise EvidenceError("DB geometry sidecar source identity differs from the runtime report")
    side_panel = _require_mapping(sidecar.get("panel"), "DB geometry sidecar panel")
    expected_panel = {
        "panel_id": panel["panel_id"], "image_sha256": panel["image_sha256"],
        "width": panel["width"], "height": panel["height"], "crop": panel["crop"],
        "requested_crop": panel["requested_crop"],
        "source_to_panel_matrix": panel["source_to_panel_matrix"],
        "panel_to_source_matrix": panel["panel_to_source_matrix"],
    }
    if side_panel != expected_panel:
        raise EvidenceError("DB geometry sidecar panel provenance differs from the runtime report")
    if (
        family_ocr._require_sha256(sidecar.get("native_sha256"), "sidecar native SHA-256") != candidate["native_sha256"]
        or sidecar.get("native_scope") != candidate["native_scope"]
    ):
        raise EvidenceError("DB geometry sidecar native identity differs from the candidate")
    if _runtime_assemblies(sidecar.get("runtime_assemblies"), "sidecar runtime assemblies") != assemblies:
        raise EvidenceError("DB geometry sidecar executed assemblies differ from the runtime report")
    detector = _require_mapping(sidecar.get("detector_model"), "DB geometry sidecar detector")
    expected_detector = candidate["detector"]
    if detector != expected_detector:
        raise EvidenceError("DB geometry sidecar detector identity differs from the candidate")

    configured = _require_mapping(panel.get("ocr_configured_models"), "configured OCR models")
    models = _require_list(configured.get("models"), "configured OCR models")
    detection_models = [item for item in models if isinstance(item, dict) and item.get("task") == "ocr_detection"]
    if len(detection_models) != 1:
        raise EvidenceError("runtime report must contain exactly one configured OCR detector")
    configured_detector = detection_models[0]
    if (
        configured_detector.get("model_id") != expected_detector["model_id"]
        or configured_detector.get("version") != expected_detector["model_version"]
        or family_ocr._require_sha256(configured_detector.get("sha256"), "configured detector SHA-256")
        != expected_detector["model_sha256"]
    ):
        raise EvidenceError("runtime report detector identity differs from the candidate")

    width = family_ocr._require_int(panel["width"], "panel width", minimum=1)
    height = family_ocr._require_int(panel["height"], "panel height", minimum=1)
    crop = _rect_tuple(panel["crop"], "panel crop")
    offset_x, offset_y = int(crop[0]), int(crop[1])
    invocations = _require_list(sidecar.get("invocations"), "DB geometry invocations")
    if len(invocations) != len(EXPECTED_INVOCATIONS):
        raise EvidenceError("DB geometry sidecar must contain exactly masked and unmasked invocations")
    result: dict[str, dict[str, tuple[Component, ...]]] = {}
    seen_ids: set[tuple[str, str]] = set()
    for invocation_index, ((expected_kind, stage), raw_invocation) in enumerate(zip(EXPECTED_INVOCATIONS, invocations)):
        invocation = _require_mapping(raw_invocation, f"DB geometry invocation {invocation_index}")
        if invocation.get("kind") != expected_kind:
            raise EvidenceError("DB geometry invocation order or kind is invalid")
        report_gray_field = "detector_input_sha256" if stage == "masked" else "unmasked_input_sha256"
        report_regions_field = "model_regions" if stage == "masked" else "unmasked_model_regions"
        gray_sha = family_ocr._require_sha256(invocation.get("canonical_gray_sha256"), f"{stage} gray SHA-256")
        if gray_sha != family_ocr._require_sha256(diagnostic.get(report_gray_field), f"report {stage} gray SHA-256"):
            raise EvidenceError(f"{stage} observation input differs from the runtime report")
        bgr_sha = family_ocr._require_sha256(invocation.get("detector_bgr_sha256"), f"{stage} BGR SHA-256")
        observation = _require_mapping(invocation.get("observation"), f"{stage} observation")
        if family_ocr._require_sha256(observation.get("input_sha256"), f"{stage} observation input SHA-256") != bgr_sha:
            raise EvidenceError(f"{stage} observation does not bind the detector-consumed BGR bytes")
        if (
            family_ocr._require_int(observation.get("image_width"), f"{stage} image width", minimum=1) != width
            or family_ocr._require_int(observation.get("image_height"), f"{stage} image height", minimum=1) != height
            or family_ocr._require_int(observation.get("tensor_width"), f"{stage} tensor width", minimum=1) <= 0
            or family_ocr._require_int(observation.get("tensor_height"), f"{stage} tensor height", minimum=1) <= 0
        ):
            raise EvidenceError(f"{stage} observation dimensions differ from the panel")
        contours = _require_list(observation.get("accepted_contours"), f"{stage} accepted contours")
        regions = _require_list(diagnostic.get(report_regions_field), f"report {stage} ModelRegions")
        if len(contours) != len(regions):
            raise EvidenceError(f"{stage} accepted-contour count differs from ModelRegions")
        initial_components: list[Component] = []
        expanded_components: list[Component] = []
        for index, (raw_contour, region) in enumerate(zip(contours, regions)):
            contour = _require_mapping(raw_contour, f"{stage} contour {index}")
            identifier = contour.get("returned_region_id")
            identity = (stage, str(identifier))
            if not identifier or identity in seen_ids:
                raise EvidenceError(f"{stage} accepted contour identities must be unique")
            seen_ids.add(identity)
            initial = _polygon_points(contour.get("initial_polygon"), f"{stage} contour {index} initial polygon", width, height)
            expanded = _validate_report_model_region(contour, region, f"{stage} contour {index}", width, height)
            initial_components.append(_component(initial, offset_x, offset_y))
            expanded_components.append(_component(expanded, offset_x, offset_y))
        result[stage] = {"initial": tuple(initial_components), "expanded": tuple(expanded_components)}
    return result


def _validate_run(
    split_name: str,
    manifest_path: Path,
    report_path: Path,
    manifest_expected_sha256: str,
    protocol_sha256: str,
    candidate: Mapping[str, Any],
    executed_assemblies: tuple[tuple[str, str], ...],
) -> tuple[
    list[dict[str, Any]], dict[str, Mapping[str, Any]], dict[str, Any], bytes,
    str, tuple[tuple[str, str], ...], int,
]:
    manifest, manifest_bytes = family_ocr._load_object(manifest_path, f"{split_name} input manifest")
    manifest_hash = family_ocr._sha256_bytes(manifest_bytes)
    if manifest_hash != manifest_expected_sha256:
        raise EvidenceError(f"{split_name} input manifest bytes differ from the protocol")
    split, dataset_seed, images = family_ocr._validate_manifest(manifest, manifest_path)
    if split != split_name:
        raise EvidenceError(f"{split_name} protocol identity points to a {split} manifest")
    report, report_bytes = family_ocr._load_object(report_path, f"{split_name} runtime report")
    cases = family_ocr._validate_report(
        report, manifest_hash, images, report_path=report_path, manifest_path=manifest_path)
    if report.get("schema") != family_ocr.REPORT_SCHEMA_V2:
        raise EvidenceError("DB geometry scoring requires a v2 panel runtime report")
    if (
        report.get("completed") != report.get("count")
        or report.get("failed") != 0
        or report.get("completed_panels") != report.get("panel_count")
        or report.get("failed_panels") != 0
    ):
        raise EvidenceError("DB geometry scoring requires every bound source and panel to complete")
    if family_ocr._require_sha256(report.get("candidate_sha256"), "report candidate SHA-256") != candidate["sha256"]:
        raise EvidenceError("runtime report does not bind the protocol candidate")
    if (
        family_ocr._require_sha256(report.get("native_sha256"), "report native SHA-256") != candidate["native_sha256"]
        or report.get("native_scope") != candidate["native_scope"]
    ):
        raise EvidenceError("runtime report native identity differs from the candidate")
    assemblies = _runtime_assemblies(report.get("runtime_assemblies"), "report runtime assemblies")
    if assemblies != executed_assemblies:
        raise EvidenceError("runtime report assemblies differ from the authenticated execution files")

    stage_predictions: dict[str, dict[str, dict[str, list[Component]]]] = {
        stage: {geometry: {} for geometry in ("initial", "expanded")}
        for _, stage in EXPECTED_INVOCATIONS
    }
    for image in images:
        source_hash = str(image["image_sha256"])
        source = cases[source_hash]
        if source.get("status") != "panels-completed":
            raise EvidenceError("DB geometry source did not complete all panels")
        for stage in stage_predictions:
            for geometry in stage_predictions[stage]:
                stage_predictions[stage][geometry][source_hash] = []
        for panel in source["panels"]:
            if panel.get("status") != "seed-completed":
                raise EvidenceError("DB geometry panel did not complete")
            panel_predictions = _validate_sidecar(
                panel, source, report, report_path, manifest_hash, protocol_sha256, candidate, assemblies)
            for stage, geometries in panel_predictions.items():
                for geometry, components in geometries.items():
                    stage_predictions[stage][geometry][source_hash].extend(components)

    return images, cases, stage_predictions, report_bytes, manifest_hash, assemblies, dataset_seed


def _metrics(
    images: Sequence[Mapping[str, Any]],
    cases: Mapping[str, Mapping[str, Any]],
    predictions: Mapping[str, Sequence[Component]],
) -> dict[str, Any]:
    truth_count = predicted_count = matches = 0
    for image in images:
        source_hash = str(image["image_sha256"])
        truths = cases[source_hash]["_db_geometry_truths"]
        source_predictions = tuple(predictions[source_hash])
        truth_count += len(truths)
        predicted_count += len(source_predictions)
        matches += maximum_cardinality_matches(source_predictions, truths)
    return {
        "truth_region_count": truth_count,
        "predicted_region_count": predicted_count,
        "true_positives": matches,
        "false_positives": predicted_count - matches,
        "false_negatives": truth_count - matches,
        "precision": matches / max(1, predicted_count),
        "recall": matches / max(1, truth_count),
    }


def score(
    protocol_path: Path,
    execution_manifest_path: Path,
    execution_manifest_sha256: str,
    train_report_path: Path,
    dev_report_path: Path,
    output_path: Path,
) -> dict[str, Any]:
    started = time.perf_counter()
    output_path = family_ocr._require_artifact_path(output_path, REPOSITORY_ROOT, "DB geometry score output")
    if output_path.exists():
        raise EvidenceError("refusing to replace an existing DB geometry score output")
    protocol, protocol_bytes, bound = _load_protocol(protocol_path)
    candidate_path, candidate_hash = bound["runtime_candidate"]
    candidate = _validate_candidate(candidate_path, candidate_hash)
    execution_manifest_bytes, executed_assemblies = _validate_execution_manifest(
        execution_manifest_path, execution_manifest_sha256)

    requested_reports = {"train": train_report_path, "validation": dev_report_path}
    manifest_keys = {"train": "train_manifest", "validation": "dev_manifest"}
    runs: dict[str, Any] = {}
    metrics: dict[str, dict[str, dict[str, Any]]] = {
        stage: {geometry: {} for geometry in ("initial", "expanded")}
        for _, stage in EXPECTED_INVOCATIONS
    }
    total_truth = 0
    combined: dict[str, dict[str, dict[str, list[Component]]]] = {
        stage: {geometry: {} for geometry in ("initial", "expanded")}
        for _, stage in EXPECTED_INVOCATIONS
    }
    combined_images: list[dict[str, Any]] = []
    combined_cases: dict[str, Mapping[str, Any]] = {}
    all_panel_ids: set[str] = set()
    common_assemblies: tuple[tuple[str, str], ...] | None = None
    validated_runs: dict[str, tuple[
        list[dict[str, Any]], dict[str, Mapping[str, Any]], dict[str, Any],
        bytes, str, int, Path, Path,
    ]] = {}
    for split_name in ("train", "validation"):
        manifest_path, manifest_hash = bound[manifest_keys[split_name]]
        report_path = family_ocr._require_artifact_path(
            requested_reports[split_name], REPOSITORY_ROOT, f"{split_name} runtime report")
        images, cases, predictions, report_bytes, validated_manifest_hash, assemblies, dataset_seed = _validate_run(
            split_name, manifest_path, report_path, manifest_hash,
            family_ocr._sha256_bytes(protocol_bytes), candidate, executed_assemblies)
        source_hashes = {str(image["image_sha256"]) for image in images}
        if source_hashes.intersection(combined_cases):
            raise EvidenceError("train and development runtime reports reuse a source identity")
        panel_ids = {
            str(panel["panel_id"])
            for source_hash in source_hashes
            for panel in cases[source_hash]["panels"]
        }
        if panel_ids.intersection(all_panel_ids):
            raise EvidenceError("train and development runtime reports reuse a panel identity")
        all_panel_ids.update(panel_ids)
        if common_assemblies is None:
            common_assemblies = assemblies
        elif assemblies != common_assemblies:
            raise EvidenceError("train and development reports used different executed assemblies")
        combined_images.extend(images)
        combined_cases.update(cases)
        validated_runs[split_name] = (
            images, cases, predictions, report_bytes, validated_manifest_hash,
            dataset_seed, manifest_path, report_path,
        )

    # Renderer truth is created only after both complete annotation-free
    # exchanges and every sidecar have passed all provenance gates.
    for split_name in ("train", "validation"):
        (
            images, cases, predictions, report_bytes, validated_manifest_hash,
            dataset_seed, manifest_path, report_path,
        ) = validated_runs[split_name]
        regenerated = family_ocr._regenerate(split_name, dataset_seed, images)
        truth_count = 0
        for image in images:
            source_hash = str(image["image_sha256"])
            _, annotation = regenerated[source_hash]
            truths, _ = family_ocr._truth_regions(annotation)
            truth_count += len(truths)
            cases[source_hash]["_db_geometry_truths"] = truths
        expected_truth = EXPECTED_TRUTH_COUNTS[split_name]
        if truth_count != expected_truth:
            raise EvidenceError(
                f"{split_name} regenerated truth count {truth_count} differs from fixed denominator {expected_truth}")
        total_truth += truth_count
        for stage in metrics:
            for geometry in metrics[stage]:
                current = _metrics(images, cases, predictions[stage][geometry])
                metrics[stage][geometry][split_name] = current
                combined[stage][geometry].update(predictions[stage][geometry])
        runs[split_name] = {
            "manifest": {"path": manifest_path.relative_to(REPOSITORY_ROOT).as_posix(), "sha256": validated_manifest_hash},
            "report": {"path": report_path.relative_to(REPOSITORY_ROOT).as_posix(), "sha256": family_ocr._sha256_bytes(report_bytes)},
            "source_count": len(images),
            "panel_count": sum(len(cases[str(image["image_sha256"])]["panels"]) for image in images),
            "truth_region_count": truth_count,
        }
    if total_truth != EXPECTED_TOTAL_TRUTHS:
        raise EvidenceError(f"combined truth count {total_truth} differs from fixed denominator {EXPECTED_TOTAL_TRUTHS}")
    for stage in metrics:
        for geometry in metrics[stage]:
            metrics[stage][geometry]["combined"] = _metrics(
                combined_images, combined_cases, combined[stage][geometry])
        initial_counts = [metrics[stage]["initial"][split]["predicted_region_count"] for split in ("train", "validation", "combined")]
        expanded_counts = [metrics[stage]["expanded"][split]["predicted_region_count"] for split in ("train", "validation", "combined")]
        if initial_counts != expanded_counts:
            raise EvidenceError(f"{stage} initial and expanded contour counts differ")

    result = {
        "schema": OUTPUT_SCHEMA,
        "status": "diagnostic_only_unapproved",
        "scope": SIDECAR_SCOPE,
        "synthetic_only": True,
        "private_data": False,
        "sealed_data": False,
        "sealed_runs": 0,
        "optimizer_steps": 0,
        "production_approval": False,
        "release_eligible": False,
        "protocol": {
            "path": PROTOCOL_PATH.as_posix(),
            "sha256": family_ocr._sha256_bytes(protocol_bytes),
            "hypothesis": protocol["hypothesis"],
            "isolated_change": protocol["isolated_change"],
        },
        "candidate": {
            "path": candidate_path.relative_to(REPOSITORY_ROOT).as_posix(),
            "sha256": candidate["sha256"],
            "native_sha256": candidate["native_sha256"],
            "native_scope": candidate["native_scope"],
            "detector": candidate["detector"],
        },
        "runs": runs,
        "execution_manifest": {
            "path": execution_manifest_path.resolve().relative_to(REPOSITORY_ROOT.resolve()).as_posix(),
            "sha256": family_ocr._sha256_bytes(execution_manifest_bytes),
            "all_declared_files_verified": True,
        },
        "integrity": {
            "protocol_and_bound_files_verified": True,
            "source_and_crop_png_bytes_verified": True,
            "sidecar_bytes_and_dimensions_verified": True,
            "native_model_manifest_and_executed_assembly_hashes_bound": True,
            "expanded_point_sequences_equal_model_regions": True,
            "initial_and_expanded_contour_counts_equal": True,
            "truth_created_only_after_exchange_validation": True,
            "full_source_truth_count": total_truth,
            "runtime_assemblies": [
                {"name": name, "sha256": assembly_sha256}
                for name, assembly_sha256 in (common_assemblies or ())
            ],
        },
        "evaluator": {
            "matching": "existing maximum-cardinality one-to-one matching",
            "intersection_over_union_minimum": MATCH_IOU_MINIMUM,
            "geometry": "axis-aligned bounds derived from the authenticated four recorded polygon points",
            "coordinate_mapping": "validated integer panel crop translation into full-source pixels",
            "confidence_scored": False,
            "recognition_scored": False,
        },
        "geometry_metrics": metrics,
        "limitations": [
            "This diagnostic compares geometry for contours already accepted by the unchanged DB detector; rejected contours are absent.",
            "Initial and expanded rotated rectangles are scored by the same axis-aligned bounds representation used by the fixed family OCR evaluator.",
            "Full-source truth remains the denominator, including text outside emitted panel crops.",
            "This synthetic train/dev diagnostic cannot approve a model, threshold, product stage, or release.",
        ],
        "elapsed_milliseconds": round((time.perf_counter() - started) * 1000.0, 3),
    }
    output_path.parent.mkdir(parents=True, exist_ok=True)
    with output_path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(result, stream, indent=2, sort_keys=True)
        stream.write("\n")
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--protocol", type=Path, required=True)
    parser.add_argument("--execution-manifest", type=Path, required=True)
    parser.add_argument("--execution-manifest-sha256", required=True)
    parser.add_argument("--train-report", type=Path, required=True)
    parser.add_argument("--dev-report", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    try:
        result = score(
            arguments.protocol,
            arguments.execution_manifest,
            arguments.execution_manifest_sha256,
            arguments.train_report,
            arguments.dev_report,
            arguments.output,
        )
    except (EvidenceError, OSError, KeyError, TypeError, json.JSONDecodeError) as exception:
        print(json.dumps({"status": "rejected", "error": str(exception)}, sort_keys=True), file=sys.stderr)
        return 1
    print(json.dumps({
        "status": result["status"],
        "full_source_truth_count": result["integrity"]["full_source_truth_count"],
        "output": str(arguments.output),
        "production_approval": False,
    }, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
