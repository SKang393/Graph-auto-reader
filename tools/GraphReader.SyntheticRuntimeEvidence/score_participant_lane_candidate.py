# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Score the development-only participant-lane OCR composition on train/dev.

The runtime report keeps the detector's raw regions and records the effective
assembled regions separately.  This adapter authenticates every annotation-free
input, independently replays the fixed assembly rule, and only then loads the
same full 709-train/183-dev truth used by the frozen V44 text metric.
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass, replace
from hashlib import sha256
import json
import math
from pathlib import Path
import time
from typing import Any, Mapping, Sequence

import score_full_ocr_candidate_v2 as metric
import score_text_extent_candidate as text_extent
from ml.ocr.official_bakeoff import text_extent_head_inputs as bridge


ROOT = Path(__file__).resolve().parents[2]
OUTPUT_SCHEMA = "graphreader.participant-lane-full-ocr-score.v1"
EVALUATION_SCHEMA = "graphreader.participant-lane-candidate-evaluation.v1"
CANDIDATE_COMPOSITION = "original-db-head-participant-lane-v1"
ASSEMBLY_COMPOSITION = "participant-lane-aligned-word-assembly-v1"
METRIC_SHA256 = "8b879664c33e83f2aaec0e637f4f0f4df0f9701fe04ac43867672dde2e662656"
TEXT_EXTENT_ADAPTER_SHA256 = "41d6438b5198c2cb7f89b1d6a26c720b4a3089d8a49666a7c7cefad5ce3fe050"
GEOMETRY_SHA256 = "719bb18c30821cdd44b65fc9ede38f6d111fb3631e07c7c69d98d1116aa575a2"
EXPECTED_BASELINE_SOURCE_MANIFEST_SHA256 = (
    "3a9e4fa83ca0fd7161875e60d2faf0729fd046f8ce714d874e329d7600cfea1c"
)
EXPECTED_HISTORICAL_CAPTURE_RUNTIME_MANIFEST_SHA256 = (
    "ed06006c6a4fcc078fc13817571a4fb58cd244add12731971ad8e7845be06aae"
)
BASELINE_SOURCE_PATHS = {
    "tools/GraphReader.SyntheticRuntimeEvidence/OfficialHeadCandidateEvaluation.cs",
    "tools/GraphReader.SyntheticRuntimeEvidence/OfficialHeadCandidateEvaluationSelfTest.cs",
    "tools/GraphReader.SyntheticRuntimeEvidence/Program.cs",
}
RUNTIME_SOURCE_PATHS = {
    "src/GraphReader.Ocr/ParticipantLaneTextRegionAssembler.cs",
    "src/GraphReader.Ocr/OcrPipeline.cs",
    "src/GraphReader.Ocr/OcrResultCache.cs",
    "src/GraphReader.App/Integration/Workflow/ProductionOriginalDbOcrCandidate.cs",
    "src/GraphReader.App/Integration/Workflow/ProductionOcrAdapter.cs",
    "tools/GraphReader.SyntheticRuntimeEvidence/OfficialHeadCandidateEvaluation.cs",
    "tools/GraphReader.SyntheticRuntimeEvidence/OfficialHeadCandidateEvaluationSelfTest.cs",
    "tools/GraphReader.SyntheticRuntimeEvidence/Program.cs",
}
HISTORICAL_RUNTIME_FILES = {
    "GraphReader.SyntheticRuntimeEvidence.dll",
    "GraphReader.App.dll",
    "GraphReader.Ocr.dll",
    "GraphReader.Inference.dll",
    "GraphReader.SyntheticRuntimeEvidence.exe",
    "GraphReader.SyntheticRuntimeEvidence.pdb",
    "GraphReader.SyntheticRuntimeEvidence.deps.json",
    "GraphReader.SyntheticRuntimeEvidence.runtimeconfig.json",
}

MINIMUM_VERTICAL_OVERLAP_RATIO = 0.35
MAXIMUM_HORIZONTAL_GAP_HEIGHT_RATIO = 2.5
MAXIMUM_COMPONENT_HEIGHT_RATIO = 2.0
MAXIMUM_MERGED_HEIGHT_GROWTH_RATIO = 1.6
MAXIMUM_VERTICAL_CENTER_OFFSET_HEIGHT_RATIO = 0.10


EvidenceError = metric.EvidenceError
Box = metric.Box
geometry = metric.geometry


@dataclass(frozen=True)
class _RawRegion:
    region_id: str
    panel_points: tuple[tuple[float, float], ...]
    source_points: tuple[tuple[float, float], ...]

    @property
    def bounds(self) -> Box:
        return _bounds(self.panel_points)


@dataclass(frozen=True)
class _AssemblyGroup:
    region_id: str
    member_ids: tuple[str, ...]
    panel_points: tuple[tuple[float, float], ...]

    @property
    def bounds(self) -> Box:
        return _bounds(self.panel_points)


@dataclass(frozen=True)
class _ValidatedEvidence:
    report: Mapping[str, Any]
    candidate: Mapping[str, Any]
    panels: Mapping[str, Any]
    raw_by_source: Mapping[str, tuple[Any, ...]]
    effective_by_source: Mapping[str, tuple[Any, ...]]
    recognized_by_source: Mapping[str, tuple[Any, ...]]
    text_predictions_by_source: Mapping[str, tuple[Any, ...]]
    explicit_region_failures: Mapping[str, int]
    failed_panel_raw_regions: Mapping[str, int]
    failed_panel_effective_regions: Mapping[str, int]
    assembly_counts: Mapping[str, Mapping[str, int]]


@dataclass(frozen=True)
class _HistoricalCaptureRuntime:
    descriptor: Mapping[str, str]
    backup_binary_root: Path
    by_original: Mapping[Path, Mapping[str, Any]]


def _descriptor(path: Path, digest: str, root: Path) -> dict[str, str]:
    return {"path": path.resolve().relative_to(root).as_posix(), "sha256": digest}


def _validate_frozen_sources(
    root: Path,
    baseline_manifest_path: Path,
    baseline_manifest_sha256: str,
    runtime_manifest_path: Path,
    runtime_manifest_sha256: str,
) -> dict[str, Any]:
    bindings = {
        "tools/GraphReader.SyntheticRuntimeEvidence/score_official_head_candidate.py":
            GEOMETRY_SHA256,
        "tools/GraphReader.SyntheticRuntimeEvidence/score_full_ocr_candidate_v2.py":
            METRIC_SHA256,
        "tools/GraphReader.SyntheticRuntimeEvidence/score_text_extent_candidate.py":
            TEXT_EXTENT_ADAPTER_SHA256,
    }
    for relative, expected in bindings.items():
        path = (root / relative).resolve()
        if not path.is_file() or sha256(path.read_bytes()).hexdigest() != expected:
            raise EvidenceError(f"frozen scoring source identity changed: {relative}")
    runtime_superseded = {
        "tools/GraphReader.SyntheticRuntimeEvidence/OfficialHeadCandidateEvaluation.cs",
    }
    metric_dependencies = {
        relative: expected
        for relative, expected in metric.SOURCE_BINDINGS.items()
        if relative not in runtime_superseded
    }
    for relative, expected in metric_dependencies.items():
        path = (root / relative).resolve()
        if not path.is_file() or sha256(path.read_bytes()).hexdigest() != expected:
            raise EvidenceError(f"frozen metric dependency identity changed: {relative}")
    try:
        geometry._validate_source_bindings(root)
    except geometry.EvidenceError as error:
        raise EvidenceError(str(error)) from error

    _, payload = geometry._read_exact(
        root, str(baseline_manifest_path), baseline_manifest_sha256,
        "pre-assembly source manifest")
    rows = geometry._array(json.loads(payload), "pre-assembly source manifest")
    if len(rows) != len(BASELINE_SOURCE_PATHS):
        raise EvidenceError("pre-assembly source manifest inventory changed")
    seen: set[str] = set()
    archived: list[dict[str, str]] = []
    for raw in rows:
        row = geometry._object(raw, "pre-assembly source row")
        if set(row) != {"backup_path", "sha256", "source_path"}:
            raise EvidenceError("pre-assembly source row has unknown or missing fields")
        source_path = row.get("source_path")
        if not isinstance(source_path, str) or source_path not in BASELINE_SOURCE_PATHS or source_path in seen:
            raise EvidenceError("pre-assembly source path is foreign or duplicated")
        backup, _ = geometry._read_exact(
            root, row.get("backup_path"), row.get("sha256"), "pre-assembly source backup")
        if (source_path == "tools/GraphReader.SyntheticRuntimeEvidence/OfficialHeadCandidateEvaluation.cs"
                and row.get("sha256") != metric.SOURCE_BINDINGS[source_path]):
            raise EvidenceError("pre-assembly evaluator differs from the frozen V44 metric binding")
        seen.add(source_path)
        archived.append({
            "source_path": source_path,
            "backup_path": backup.relative_to(root).as_posix(),
            "sha256": geometry._sha(row.get("sha256"), "pre-assembly source backup"),
        })
    if seen != BASELINE_SOURCE_PATHS:
        raise EvidenceError("pre-assembly source manifest is incomplete")
    _, runtime_payload = geometry._read_exact(
        root, str(runtime_manifest_path), runtime_manifest_sha256,
        "participant-lane runtime source manifest")
    runtime_rows = geometry._array(
        json.loads(runtime_payload), "participant-lane runtime source manifest")
    if len(runtime_rows) != len(RUNTIME_SOURCE_PATHS):
        raise EvidenceError("participant-lane runtime source inventory changed")
    runtime_sources: list[dict[str, str]] = []
    runtime_seen: set[str] = set()
    for raw in runtime_rows:
        row = geometry._object(raw, "participant-lane runtime source row")
        if set(row) != {"path", "sha256"}:
            raise EvidenceError("participant-lane runtime source row has unknown or missing fields")
        relative = row.get("path")
        if (not isinstance(relative, str) or relative not in RUNTIME_SOURCE_PATHS
                or relative in runtime_seen):
            raise EvidenceError("participant-lane runtime source is foreign or duplicated")
        path, _ = geometry._read_exact(
            root, relative, row.get("sha256"), "participant-lane runtime source")
        runtime_seen.add(relative)
        runtime_sources.append({
            "path": path.relative_to(root).as_posix(),
            "sha256": geometry._sha(row.get("sha256"), "participant-lane runtime source"),
        })
    if runtime_seen != RUNTIME_SOURCE_PATHS:
        raise EvidenceError("participant-lane runtime source manifest is incomplete")
    return {
        "active_frozen_scorers": bindings,
        "active_frozen_metric_dependencies": metric_dependencies,
        "runtime_sources_superseded_by_candidate_bound_assemblies": sorted(runtime_superseded),
        "pre_assembly_source_manifest": _descriptor(
            baseline_manifest_path, baseline_manifest_sha256, root),
        "pre_assembly_sources": sorted(archived, key=lambda item: item["source_path"]),
        "participant_lane_runtime_source_manifest": _descriptor(
            runtime_manifest_path, runtime_manifest_sha256, root),
        "participant_lane_runtime_sources": sorted(
            runtime_sources, key=lambda item: item["path"]),
    }


def _validate_candidate(root: Path, path: Path, expected_sha256: str) -> Mapping[str, Any]:
    _, payload = geometry._read_exact(root, str(path), expected_sha256, "candidate")
    candidate = geometry._json(payload, "candidate")
    required = {
        "schema", "scope", "production_approved", "training_input_ready",
        "composition_version", "native_path", "native_sha256", "native_scope",
        "license_inputs", "detector", "recognizer", "execution_assemblies",
    }
    if set(candidate) != required:
        raise EvidenceError("candidate has unknown or missing fields")
    if (candidate.get("schema") != geometry.CANDIDATE_SCHEMA
            or candidate.get("scope") != geometry.CANDIDATE_SCOPE
            or candidate.get("production_approved") is not False
            or candidate.get("training_input_ready") is not False
            or candidate.get("composition_version") != CANDIDATE_COMPOSITION
            or candidate.get("native_scope") != "reviewed-source-runtime-local-diagnostic"):
        raise EvidenceError("participant-lane candidate scope or composition is invalid")
    geometry._read_exact(
        root, candidate["native_path"], candidate["native_sha256"], "candidate native")
    geometry._validate_assemblies(
        root, candidate["execution_assemblies"], "candidate execution assemblies")
    licenses = geometry._array(candidate["license_inputs"], "candidate license inputs")
    if not licenses:
        raise EvidenceError("candidate has no license inputs")
    license_keys: set[tuple[str, str]] = set()
    for raw in licenses:
        row = geometry._object(raw, "candidate license input")
        if set(row) != {"path", "sha256"}:
            raise EvidenceError("candidate license input has unknown or missing fields")
        bound, _ = geometry._read_exact(
            root, row["path"], row["sha256"], "candidate license input")
        key = (str(bound).lower(), geometry._sha(row["sha256"], "candidate license input"))
        if key in license_keys:
            raise EvidenceError("candidate repeats a license input")
        license_keys.add(key)

    model_hashes: set[str] = set()
    for role, task in (("detector", "ocr_detection"), ("recognizer", "ocr_recognition")):
        model = geometry._object(candidate[role], f"candidate {role}")
        required_model = {
            "model_path", "model_id", "model_version", "model_sha256",
            "manifest_path", "manifest_sha256",
        }
        if set(model) != required_model:
            raise EvidenceError(f"candidate {role} has unknown or missing fields")
        geometry._read_exact(
            root, model["model_path"], model["model_sha256"], f"candidate {role} model")
        _, manifest_payload = geometry._read_exact(
            root, model["manifest_path"], model["manifest_sha256"],
            f"candidate {role} manifest")
        manifest = geometry._json(manifest_payload, f"candidate {role} manifest")
        if (manifest.get("model_id") != model.get("model_id")
                or manifest.get("model_version") != model.get("model_version")
                or manifest.get("task") != task
                or manifest.get("sha256") != model.get("model_sha256")
                or "cpu" not in [str(item).lower() for item in geometry._array(
                    manifest.get("providers"), f"{role} providers")]):
            raise EvidenceError(f"candidate {role} manifest identity changed")
        model_hashes.add(geometry._sha(model["model_sha256"], f"candidate {role} model"))
    if len(model_hashes) != 2:
        raise EvidenceError("candidate detector and recognizer payloads are not distinct")
    return candidate


def _validate_historical_capture_runtime(
    root: Path,
    manifest_path: Path,
    manifest_sha256: str,
) -> _HistoricalCaptureRuntime:
    _, payload = geometry._read_exact(
        root, str(manifest_path), manifest_sha256, "historical capture runtime manifest")
    rows = geometry._array(json.loads(payload), "historical capture runtime manifest")
    if len(rows) != len(HISTORICAL_RUNTIME_FILES):
        raise EvidenceError("historical capture runtime inventory changed")
    by_original: dict[Path, Mapping[str, Any]] = {}
    backups: set[Path] = set()
    observed_names: set[str] = set()
    for raw in rows:
        row = geometry._object(raw, "historical capture runtime row")
        if set(row) != {"sha256", "backup_path", "original_path"}:
            raise EvidenceError("historical capture runtime row has unknown or missing fields")
        original = geometry._inside(root, row.get("original_path"), "historical runtime original")
        backup, _ = geometry._read_exact(
            root, row.get("backup_path"), row.get("sha256"), "historical runtime backup")
        name = original.name
        if (name not in HISTORICAL_RUNTIME_FILES or name in observed_names
                or original in by_original or backup in backups):
            raise EvidenceError("historical capture runtime path is foreign or duplicated")
        observed_names.add(name)
        by_original[original] = {
            "backup": backup,
            "sha256": geometry._sha(row.get("sha256"), "historical runtime backup"),
        }
        backups.add(backup)
    if observed_names != HISTORICAL_RUNTIME_FILES:
        raise EvidenceError("historical capture runtime manifest is incomplete")
    dll_backups = {
        Path(row["backup"]).parent
        for original, row in by_original.items()
        if original.suffix.lower() == ".dll"
    }
    if len(dll_backups) != 1:
        raise EvidenceError("historical capture assemblies do not share one backup directory")
    return _HistoricalCaptureRuntime(
        _descriptor(manifest_path, manifest_sha256, root),
        next(iter(dll_backups)), by_original)


def _validate_historical_capture_assemblies(
    root: Path,
    value: Any,
    historical: _HistoricalCaptureRuntime,
) -> tuple[dict[str, str], ...]:
    records = geometry._array(value, "capture assemblies")
    if len(records) != 4:
        raise EvidenceError("capture assemblies must contain four assemblies")
    result: list[dict[str, str]] = []
    seen: set[str] = set()
    roots: set[Path] = set()
    for raw in records:
        row = geometry._object(raw, "capture assembly")
        if set(row) != {"name", "path", "sha256"}:
            raise EvidenceError("capture assembly has unknown or missing fields")
        name = row.get("name")
        if not isinstance(name, str) or name not in geometry.EXPECTED_ASSEMBLIES or name in seen:
            raise EvidenceError("capture assembly names are incomplete or duplicated")
        original = geometry._inside(root, row.get("path"), f"capture assembly {name}")
        digest = geometry._sha(row.get("sha256"), f"capture assembly {name}")
        preserved = historical.by_original.get(original)
        if preserved is None or preserved["sha256"] != digest:
            raise EvidenceError(f"capture assembly {name} has no exact historical backup")
        backup = Path(preserved["backup"])
        if sha256(backup.read_bytes()).hexdigest() != digest:
            raise EvidenceError(f"capture assembly {name} historical backup changed")
        roots.add(original.parent)
        seen.add(name)
        result.append({
            "name": name, "path": original.relative_to(root).as_posix(), "sha256": digest,
        })
    if seen != geometry.EXPECTED_ASSEMBLIES or len(roots) != 1:
        raise EvidenceError("capture assemblies do not identify one complete historical snapshot")
    return tuple(sorted(result, key=lambda item: item["name"]))


def _validate_annotation_free_inputs(
    root: Path,
    binding_path: Path,
    binding_sha256: str,
    capture_report_path: Path,
    capture_report_sha256: str,
    request_path: Path,
    request_sha256: str,
    candidate_path: Path,
    candidate_sha256: str,
    evaluation_path: Path,
    evaluation_sha256: str,
    historical_capture_runtime: _HistoricalCaptureRuntime,
) -> _ValidatedEvidence:
    geometry._read_exact(root, str(binding_path), binding_sha256, "V3 binding")
    resolved_request, request_payload = geometry._read_exact(
        root, str(request_path), request_sha256, "capture request")
    request = geometry._json(request_payload, "capture request")
    if (request.get("schema") != geometry.CAPTURE_REQUEST_SCHEMA
            or request.get("scope") != geometry.production_head_inputs.CAPTURE_SCOPE
            or request.get("synthetic_only") is not True
            or request.get("private_data") is not False
            or request.get("sealed_data") is not False
            or request.get("truth_included") is not False
            or request.get("model_inference") is not False
            or request.get("production_approved") is not False
            or request.get("binding") != {
                "path": binding_path.resolve().relative_to(root).as_posix(),
                "sha256": binding_sha256,
            }):
        raise EvidenceError("capture request scope or binding changed")
    geometry._descriptor(root, request.get("capture_source"), "capture source")
    _validate_historical_capture_assemblies(
        root, request.get("assemblies"), historical_capture_runtime)
    geometry._descriptor(root, request.get("candidate"), "capture baseline candidate")
    detector = geometry._object(request.get("detector"), "capture detector")
    if (detector.get("model_id") != "PP-OCRv5_mobile_det"
            or detector.get("model_version") != "5.0.0"
            or detector.get("model_sha256") != geometry.PINNED_CAPTURE_MODEL_SHA256):
        raise EvidenceError("capture request detector is not the pinned official model")
    geometry._validate_capture_detector_model(
        root, detector.get("model_path"), detector.get("model_sha256"))
    geometry._read_exact(
        root, detector.get("manifest_path"), detector.get("manifest_sha256"),
        "capture detector manifest")
    native = geometry._object(request.get("native"), "capture native")
    geometry._read_historical_capture_artifact(
        root, native.get("path"), native.get("sha256"), "capture native")
    if (request.get("maximum_side_length")
            != geometry.production_head_inputs.MAXIMUM_SIDE_LENGTH
            or request.get("dimension_multiple")
            != geometry.production_head_inputs.DIMENSION_MULTIPLE
            or request.get("detector_configuration_fingerprint")
            != geometry.production_head_inputs.DETECTOR_CONFIGURATION_FINGERPRINT):
        raise EvidenceError("capture request detector preprocessing identity changed")
    for raw in geometry._array(request.get("license_inputs"), "capture license inputs"):
        row = geometry._object(raw, "capture license input")
        geometry._read_historical_capture_artifact(
            root, row.get("path"), row.get("sha256"), "capture license input")

    panels = geometry._runtime_panels(root, request)
    plot_bounds = _runtime_plot_bounds(root, request, panels)
    candidate = _validate_candidate(root, candidate_path, candidate_sha256)
    geometry._prevalidate_capture_report(
        root, capture_report_path, capture_report_sha256,
        resolved_request, request_sha256, request, panels)
    return _validate_evaluation(
        root, evaluation_path, evaluation_sha256, resolved_request, request_sha256,
        candidate_path, candidate_sha256, panels, plot_bounds, candidate)


def _runtime_plot_bounds(
    root: Path,
    request: Mapping[str, Any],
    panels: Mapping[str, Any],
) -> dict[str, tuple[float, float, float, float]]:
    result: dict[str, tuple[float, float, float, float]] = {}
    for raw_descriptor in geometry._array(request.get("reports"), "capture reports"):
        descriptor = geometry._object(raw_descriptor, "capture report")
        _, payload = geometry._read_exact(
            root, descriptor.get("report_path"), descriptor.get("report_sha256"),
            "runtime report")
        report = geometry._json(payload, "runtime report")
        for raw_case in geometry._array(report.get("cases"), "runtime cases"):
            case = geometry._object(raw_case, "runtime case")
            for raw_panel in geometry._array(case.get("panels"), "runtime panels"):
                panel = geometry._object(raw_panel, "runtime panel")
                panel_id = panel.get("panel_id")
                expected = panels.get(panel_id) if isinstance(panel_id, str) else None
                if expected is None or panel_id in result:
                    raise EvidenceError("runtime plot panel is foreign or duplicated")
                axis = geometry._object(panel.get("axis"), "runtime axis")
                axis_geometry = geometry._object(axis.get("geometry"), "runtime axis geometry")
                if axis_geometry.get("coordinate_space") != "original_pixels":
                    raise EvidenceError("runtime plot geometry is not in panel original pixels")
                points = geometry._polygon(
                    axis_geometry.get("plot_polygon"), "runtime plot polygon",
                    expected.width, expected.height)
                if len(points) != 4:
                    raise EvidenceError("runtime plot polygon must contain four points")
                box = _bounds(points)
                result[panel_id] = (box.left, box.top, box.right, box.bottom)
    if set(result) != set(panels):
        raise EvidenceError("runtime plot inventory differs from the capture panel inventory")
    return result


def _bounds(points: Sequence[tuple[float, float]]) -> Box:
    return Box(
        min(x for x, _ in points), min(y for _, y in points),
        max(x for x, _ in points), max(y for _, y in points))


def _rectangle_points(box: Box) -> tuple[tuple[float, float], ...]:
    return (
        (box.left, box.top), (box.right, box.top),
        (box.right, box.bottom), (box.left, box.bottom),
    )


def _union(left: Box, right: Box) -> Box:
    return Box(
        min(left.left, right.left), min(left.top, right.top),
        max(left.right, right.right), max(left.bottom, right.bottom))


def _can_merge(left: Box, right: Box) -> bool:
    minimum_height = min(left.height, right.height)
    maximum_height = max(left.height, right.height)
    vertical_overlap = max(0.0, min(left.bottom, right.bottom) - max(left.top, right.top))
    horizontal_gap = (
        left.left - right.right if left.left > right.right
        else right.left - left.right if right.left > left.right
        else 0.0
    )
    merged = _union(left, right)
    center_offset = abs((left.top + left.bottom) / 2.0 - (right.top + right.bottom) / 2.0)
    return (
        maximum_height / minimum_height <= MAXIMUM_COMPONENT_HEIGHT_RATIO
        and vertical_overlap / minimum_height >= MINIMUM_VERTICAL_OVERLAP_RATIO
        and horizontal_gap / maximum_height <= MAXIMUM_HORIZONTAL_GAP_HEIGHT_RATIO
        and merged.height / maximum_height <= MAXIMUM_MERGED_HEIGHT_GROWTH_RATIO
        and center_offset / maximum_height <= MAXIMUM_VERTICAL_CENTER_OFFSET_HEIGHT_RATIO
    )


def _inside_participant_lane(box: Box, plot: tuple[float, float, float, float]) -> bool:
    center_x = (box.left + box.right) / 2.0
    center_y = (box.top + box.bottom) / 2.0
    return center_x < plot[0] and plot[1] <= center_y <= plot[3]


def _merged_id(member_ids: Sequence[str]) -> str:
    material = ASSEMBLY_COMPOSITION + "\n" + "\n".join(sorted(member_ids))
    return "participant-lane:" + sha256(material.encode("utf-8")).hexdigest()


def _replay_groups(
    raw_regions: Sequence[_RawRegion],
    plot: tuple[float, float, float, float],
) -> tuple[_AssemblyGroup, ...]:
    remaining = [
        _AssemblyGroup(row.region_id, (row.region_id,), row.panel_points)
        for row in raw_regions
    ]
    remaining.sort(key=lambda row: (
        row.bounds.top, row.bounds.left, row.bounds.bottom, row.bounds.right, row.region_id))
    assembled: list[_AssemblyGroup] = []
    while remaining:
        line = remaining.pop(0)
        changed = True
        while changed:
            changed = False
            for index in range(len(remaining) - 1, -1, -1):
                candidate = remaining[index]
                merged = _union(line.bounds, candidate.bounds)
                if not _can_merge(line.bounds, candidate.bounds):
                    continue
                if not _inside_participant_lane(merged, plot):
                    continue
                members = tuple(sorted((*line.member_ids, *candidate.member_ids)))
                line = _AssemblyGroup(
                    _merged_id(members), members, _rectangle_points(merged))
                remaining.pop(index)
                changed = True
        assembled.append(line)
    return tuple(sorted(assembled, key=lambda row: (
        row.bounds.top, row.bounds.left, row.bounds.bottom, row.bounds.right, row.region_id)))


def _same_values(left: Sequence[float], right: Sequence[float]) -> bool:
    return len(left) == len(right) and all(abs(a - b) <= 1e-9 for a, b in zip(left, right))


def _same_points(
    left: Sequence[tuple[float, float]],
    right: Sequence[tuple[float, float]],
) -> bool:
    return len(left) == len(right) and all(
        abs(ax - bx) <= 1e-9 and abs(ay - by) <= 1e-9
        for (ax, ay), (bx, by) in zip(left, right)
    )


def _validate_panel_regions(
    record: Mapping[str, Any],
    expected: Any,
    plot: tuple[float, float, float, float],
) -> tuple[
    tuple[_RawRegion, ...],
    tuple[_AssemblyGroup, ...],
    tuple[metric.FullTextPrediction, ...],
    int,
]:
    context = geometry._object(record.get("assembly_context"), "assembly context")
    if set(context) != {"composition_version", "plot_bounds_panel_ltrb"}:
        raise EvidenceError("assembly context has unknown or missing fields")
    context_plot = geometry._array(
        context.get("plot_bounds_panel_ltrb"), "assembly plot bounds")
    if (context.get("composition_version") != ASSEMBLY_COMPOSITION
            or len(context_plot) != 4
            or not _same_values(
                tuple(geometry._finite(value, "assembly plot bound") for value in context_plot),
                plot)):
        raise EvidenceError("assembly context differs from authenticated runtime plot bounds")

    raw_regions: list[_RawRegion] = []
    raw_ids: set[str] = set()
    for raw in geometry._array(record.get("raw_detector_regions"), "raw detector regions"):
        row = geometry._object(raw, "raw detector region")
        region_id = row.get("region_id")
        if not isinstance(region_id, str) or not region_id or region_id in raw_ids:
            raise EvidenceError("raw detector region identity is missing or duplicated")
        panel_points = geometry._polygon(
            row.get("panel_polygon"), "raw panel polygon", expected.width, expected.height)
        source_points = geometry._polygon(
            row.get("source_polygon"), "raw source polygon",
            expected.source_width, expected.source_height)
        if (row.get("coordinate_space") != "source_original_pixels"
                or not _same_points(
                    geometry._mapped(panel_points, expected.panel_to_source_matrix),
                    source_points)):
            raise EvidenceError("raw detector source geometry differs from the authenticated transform")
        raw_ids.add(region_id)
        raw_regions.append(_RawRegion(region_id, panel_points, source_points))

    status = record.get("status")
    raw_effective = geometry._array(record.get("effective_regions"), "effective regions")
    if status == "completed" or raw_effective:
        expected_groups = _replay_groups(raw_regions, plot)
        if len(raw_effective) != len(expected_groups):
            raise EvidenceError("effective region count differs from deterministic assembly replay")
    else:
        expected_groups = ()

    effective: list[_AssemblyGroup] = []
    effective_ids: set[str] = set()
    for raw, replayed in zip(raw_effective, expected_groups):
        row = geometry._object(raw, "effective region")
        if set(row) != {
            "region_id", "member_raw_region_ids", "assembly_kind",
            "coordinate_space", "panel_polygon", "source_polygon",
        }:
            raise EvidenceError("effective region has unknown or missing fields")
        region_id = row.get("region_id")
        members = geometry._array(row.get("member_raw_region_ids"), "effective members")
        if (not isinstance(region_id, str) or not region_id or region_id in effective_ids
                or not members or any(not isinstance(item, str) or not item for item in members)
                or list(members) != sorted(set(members))):
            raise EvidenceError("effective region identity or membership is invalid")
        panel_points = geometry._polygon(
            row.get("panel_polygon"), "effective panel polygon", expected.width, expected.height)
        source_points = geometry._polygon(
            row.get("source_polygon"), "effective source polygon",
            expected.source_width, expected.source_height)
        expected_kind = "identity" if len(replayed.member_ids) == 1 else "participant_lane"
        if (region_id != replayed.region_id
                or tuple(members) != replayed.member_ids
                or row.get("assembly_kind") != expected_kind
                or row.get("coordinate_space") != "source_original_pixels"
                or not _same_points(panel_points, replayed.panel_points)
                or not _same_points(
                    geometry._mapped(panel_points, expected.panel_to_source_matrix),
                    source_points)):
            raise EvidenceError("effective region differs from deterministic assembly replay")
        effective_ids.add(region_id)
        effective.append(_AssemblyGroup(region_id, tuple(members), panel_points))

    recognized_ids: set[str] = set()
    predictions: list[metric.FullTextPrediction] = []
    for raw in geometry._array(record.get("recognized_regions"), "recognized regions"):
        row = geometry._object(raw, "recognized region")
        region_id = row.get("region_id")
        if (not isinstance(region_id, str) or not region_id
                or region_id in recognized_ids or region_id not in effective_ids):
            raise EvidenceError(
                "recognized region identity is missing, duplicated, or absent from effective output")
        recognized_ids.add(region_id)
        expected_group = next(group for group in effective if group.region_id == region_id)
        panel_points = geometry._polygon(
            row.get("panel_polygon"), "recognized panel polygon", expected.width, expected.height)
        source_points = geometry._polygon(
            row.get("source_polygon"), "recognized source polygon",
            expected.source_width, expected.source_height)
        text = row.get("text")
        role = row.get("role")
        if (row.get("coordinate_space") != "source_original_pixels"
                or not _same_points(panel_points, expected_group.panel_points)
                or not _same_points(
                    geometry._mapped(panel_points, expected.panel_to_source_matrix),
                    source_points)):
            raise EvidenceError("recognized geometry differs from its effective region")
        if not isinstance(text, str) or role not in metric.RUNTIME_ROLES:
            raise EvidenceError("recognized text or runtime role is invalid")
        predictions.append(metric.FullTextPrediction(
            f"{expected.panel_id}\n{region_id}", expected.source_sha256,
            geometry._prediction(source_points).box, text, role))

    explicit_failures = 0
    if status == "completed":
        failures = geometry._array(record.get("region_failures"), "recognition failures")
        failure_ids: list[str] = []
        for raw in failures:
            row = geometry._object(raw, "recognition failure")
            region_id = row.get("region_id")
            if not isinstance(region_id, str) or not region_id:
                raise EvidenceError("recognition failure lacks an effective region identity")
            failure_ids.append(region_id)
        if (len(failure_ids) != len(set(failure_ids))
                or set(failure_ids) & recognized_ids
                or set(failure_ids) | recognized_ids != effective_ids):
            raise EvidenceError(
                "recognized regions and explicit failures do not partition effective output")
        explicit_failures = len(failure_ids)
    elif recognized_ids:
        raise EvidenceError("failed panel unexpectedly retains recognized regions")
    return tuple(raw_regions), tuple(effective), tuple(predictions), explicit_failures


def _validate_evaluation(
    root: Path,
    report_path: Path,
    expected_sha256: str,
    request_path: Path,
    request_sha256: str,
    candidate_path: Path,
    candidate_sha256: str,
    panels: Mapping[str, Any],
    plots: Mapping[str, tuple[float, float, float, float]],
    candidate: Mapping[str, Any],
) -> _ValidatedEvidence:
    _, payload = geometry._read_exact(
        root, str(report_path), expected_sha256, "participant-lane evaluation report")
    report = geometry._json(payload, "participant-lane evaluation report")
    if (report.get("schema") != EVALUATION_SCHEMA
            or report.get("development_only") is not True
            or report.get("scope") != geometry.CANDIDATE_SCOPE
            or report.get("synthetic_only") is not True
            or report.get("private_data") is not False
            or report.get("sealed_data") is not False
            or report.get("truth_used_by_runtime") is not False
            or report.get("optimizer_steps") != 0
            or report.get("production_approved") is not False
            or report.get("training_input_ready") is not False
            or report.get("model_inference") is not True
            or report.get("input_mode") != "production_decoded_original_bgr_db"
            or report.get("detector_postprocess") != "manifest_bound_unchanged_db_postprocess"
            or report.get("maximum_logical_detector_requests_per_panel") != 2
            or report.get("detector_requests_share_exact_runtime_input_and_stage_cache_key") is not True
            or report.get("graph_structure_consensus_applied") is not False
            or report.get("axis_mask_applied_to_detector") is not False
            or report.get("axis_bounds_used_for_role_classification") is not True):
        raise EvidenceError("participant-lane evaluation scope or isolated behavior is invalid")
    request_descriptor = geometry._object(report.get("request"), "evaluation request")
    if (geometry._inside(root, request_descriptor.get("path"), "evaluation request")
            != request_path or request_descriptor.get("sha256") != request_sha256):
        raise EvidenceError("candidate evaluation request binding changed")

    candidate_descriptor = geometry._object(report.get("candidate"), "evaluation candidate")
    if set(candidate_descriptor) != {
        "path", "sha256", "composition_version", "adapter_id", "configuration_scope",
        "detector", "recognizer", "native_sha256",
    }:
        raise EvidenceError("evaluation candidate descriptor has unknown or missing fields")
    detector = geometry._object(candidate.get("detector"), "candidate detector")
    recognizer = geometry._object(candidate.get("recognizer"), "candidate recognizer")
    expected_detector = {
        "task": "ocr_detection", "model_id": detector.get("model_id"),
        "version": detector.get("model_version"), "sha256": detector.get("model_sha256"),
        "manifest_path": geometry._inside(
            root, detector.get("manifest_path"), "detector manifest").relative_to(root).as_posix(),
        "manifest_sha256": detector.get("manifest_sha256"),
    }
    expected_recognizer = {
        "task": "ocr_recognition", "model_id": recognizer.get("model_id"),
        "version": recognizer.get("model_version"), "sha256": recognizer.get("model_sha256"),
        "manifest_path": geometry._inside(
            root, recognizer.get("manifest_path"), "recognizer manifest").relative_to(root).as_posix(),
        "manifest_sha256": recognizer.get("manifest_sha256"),
    }
    expected_adapter = (
        f"graphreader-ocr:{CANDIDATE_COMPOSITION}:"
        f"{str(detector.get('model_sha256'))[:12]}:{str(recognizer.get('model_sha256'))[:12]}:"
        f"{str(candidate.get('native_sha256'))[:12]}"
    )
    if (geometry._inside(root, candidate_descriptor.get("path"), "evaluation candidate")
            != candidate_path
            or candidate_descriptor.get("sha256") != candidate_sha256
            or candidate_descriptor.get("composition_version") != CANDIDATE_COMPOSITION
            or candidate_descriptor.get("adapter_id") != expected_adapter
            or candidate_descriptor.get("configuration_scope") != "unapproved_frozen_candidate"
            or candidate_descriptor.get("native_sha256") != candidate.get("native_sha256")
            or candidate_descriptor.get("detector") != expected_detector
            or candidate_descriptor.get("recognizer") != expected_recognizer):
        raise EvidenceError("participant-lane evaluation identity changed")
    evaluation_assemblies = geometry._validate_assemblies(
        root, report.get("execution_assemblies"), "evaluation execution assemblies")
    candidate_assemblies = geometry._validate_assemblies(
        root, candidate.get("execution_assemblies"), "candidate execution assemblies")
    if evaluation_assemblies != candidate_assemblies:
        raise EvidenceError("evaluation execution assemblies differ from candidate")

    records = geometry._array(report.get("panels"), "evaluation panels")
    panel_count = geometry._integer(report.get("panel_count"), "evaluation panel count")
    completed_count = geometry._integer(report.get("completed_panel_count"), "completed panel count")
    failed_count = geometry._integer(report.get("failed_panel_count"), "failed panel count")
    if (panel_count != len(panels) or len(records) != len(panels)
            or completed_count + failed_count != len(panels)
            or report.get("status") != ("panels_completed" if failed_count == 0 else "failed")):
        raise EvidenceError("participant-lane evaluation panel counts are inconsistent")

    raw_by_source: dict[str, list[Any]] = {}
    effective_by_source: dict[str, list[Any]] = {}
    recognized_by_source: dict[str, list[Any]] = {}
    text_by_source: dict[str, list[Any]] = {}
    explicit = {"train": 0, "validation": 0}
    failed_raw = {"train": 0, "validation": 0}
    failed_effective = {"train": 0, "validation": 0}
    counts = {
        "train": {"raw": 0, "effective": 0, "identity": 0, "participant_lane": 0},
        "validation": {"raw": 0, "effective": 0, "identity": 0, "participant_lane": 0},
    }
    seen: set[str] = set()
    completed = failed = 0
    for raw_record in records:
        record = geometry._object(raw_record, "evaluation panel")
        panel_id = record.get("panel_id")
        expected = panels.get(panel_id) if isinstance(panel_id, str) else None
        if expected is None or panel_id in seen:
            raise EvidenceError("evaluation panel identity is foreign or duplicated")
        seen.add(panel_id)
        if (record.get("split") != expected.split
                or record.get("source_sha256") != expected.source_sha256
                or record.get("source_width") != expected.source_width
                or record.get("source_height") != expected.source_height
                or record.get("panel_sha256") != expected.panel_sha256
                or record.get("width") != expected.width or record.get("height") != expected.height
                or geometry._box(record.get("crop"), "evaluation crop") != expected.crop
                or geometry._box(record.get("requested_crop"), "evaluation requested crop")
                != expected.requested_crop
                or geometry._matrix(record.get("source_to_panel_matrix"), "evaluation source-to-panel")
                != expected.source_to_panel_matrix
                or geometry._matrix(record.get("panel_to_source_matrix"), "evaluation panel-to-source")
                != expected.panel_to_source_matrix):
            raise EvidenceError("evaluation panel provenance differs from the capture request")
        status = record.get("status")
        if status not in {"completed", "failed"}:
            raise EvidenceError("evaluation panel status is invalid")
        if status == "completed":
            completed += 1
            if (record.get("original_gray_sha256") != expected.gray_sha256
                    or record.get("original_bgr_sha256") != expected.bgr_sha256):
                raise EvidenceError("evaluation decoded pixel identity changed")
        else:
            failed += 1

        raw_regions, effective_regions, text_predictions, failure_count = _validate_panel_regions(
            record, expected, plots[panel_id])
        for row in raw_regions:
            raw_by_source.setdefault(expected.source_sha256, []).append(
                geometry._prediction(row.source_points))
        for row in effective_regions:
            source_points = geometry._mapped(row.panel_points, expected.panel_to_source_matrix)
            effective_by_source.setdefault(expected.source_sha256, []).append(
                geometry._prediction(source_points))
        for row in text_predictions:
            recognized_by_source.setdefault(expected.source_sha256, []).append(
                geometry._Prediction(row.box))
            text_by_source.setdefault(expected.source_sha256, []).append(row)
        counts[expected.split]["raw"] += len(raw_regions)
        counts[expected.split]["effective"] += len(effective_regions)
        counts[expected.split]["identity"] += sum(
            len(row.member_ids) == 1 for row in effective_regions)
        counts[expected.split]["participant_lane"] += sum(
            len(row.member_ids) > 1 for row in effective_regions)
        if status == "completed":
            explicit[expected.split] += failure_count
        else:
            failed_raw[expected.split] += len(raw_regions)
            failed_effective[expected.split] += len(effective_regions)
    if seen != set(panels) or completed != completed_count or failed != failed_count:
        raise EvidenceError("participant-lane evaluation did not retain every panel exactly once")
    return _ValidatedEvidence(
        report, candidate, panels,
        {key: tuple(value) for key, value in raw_by_source.items()},
        {key: tuple(value) for key, value in effective_by_source.items()},
        {key: tuple(value) for key, value in recognized_by_source.items()},
        {key: tuple(value) for key, value in text_by_source.items()},
        explicit, failed_raw, failed_effective, counts)


def _geometry_metrics(
    evidence: _ValidatedEvidence,
    truths_by_split: Mapping[str, Sequence[Any]],
) -> tuple[dict[str, Any], dict[str, Any], dict[str, Any], dict[str, Any]]:
    raw_metrics: dict[str, Any] = {}
    effective_metrics: dict[str, Any] = {}
    recognized_metrics: dict[str, Any] = {}
    failures: dict[str, Any] = {}
    for split, truths in truths_by_split.items():
        sources = {truth.source_sha256 for truth in truths}
        select = lambda rows: {key: value for key, value in rows.items() if key in sources}
        raw_metrics[split] = geometry._score_predictions(truths, select(evidence.raw_by_source))
        effective_metrics[split] = geometry._score_predictions(
            truths, select(evidence.effective_by_source))
        recognized_metrics[split] = geometry._score_predictions(
            truths, select(evidence.recognized_by_source))
        failures[split] = {
            "failed_panel_count": sum(
                panel.split == split
                for panel_id, panel in evidence.panels.items()
                if next(row for row in evidence.report["panels"]
                        if row["panel_id"] == panel_id)["status"] == "failed"
            ),
            "explicit_effective_region_failure_count": evidence.explicit_region_failures[split],
            "raw_regions_on_failed_panels": evidence.failed_panel_raw_regions[split],
            "effective_regions_on_failed_panels": evidence.failed_panel_effective_regions[split],
            "effective_regions_without_successful_recognition": (
                effective_metrics[split]["predicted_region_count"]
                - recognized_metrics[split]["predicted_region_count"]
            ),
        }
    return raw_metrics, effective_metrics, recognized_metrics, failures


def score(
    summary_path: Path,
    summary_sha256: str,
    candidate_path: Path,
    candidate_sha256: str,
    evaluation_path: Path,
    evaluation_sha256: str,
    baseline_source_manifest_path: Path,
    baseline_source_manifest_sha256: str,
    runtime_source_manifest_path: Path,
    runtime_source_manifest_sha256: str,
    historical_capture_runtime_manifest_path: Path,
    historical_capture_runtime_manifest_sha256: str,
    output_path: Path,
    *,
    source_sha256: str,
    repository_root: Path = ROOT,
) -> Mapping[str, Any]:
    started = time.perf_counter()
    root = repository_root.resolve()
    source_digest = geometry._sha(source_sha256, "scoring adapter SHA-256")
    if sha256(Path(__file__).read_bytes()).hexdigest() != source_digest:
        raise EvidenceError("participant-lane scoring adapter identity changed")
    output = output_path.resolve()
    artifacts = (root / "artifacts").resolve()
    if output == artifacts or artifacts not in output.parents or output.exists():
        raise EvidenceError("score output must be a new file under repository artifacts")
    baseline_manifest_digest = geometry._sha(
        baseline_source_manifest_sha256, "baseline source manifest SHA-256")
    if baseline_manifest_digest != EXPECTED_BASELINE_SOURCE_MANIFEST_SHA256:
        raise EvidenceError("baseline source manifest is not the pinned pre-assembly snapshot")
    historical_runtime_digest = geometry._sha(
        historical_capture_runtime_manifest_sha256,
        "historical capture runtime manifest SHA-256")
    if historical_runtime_digest != EXPECTED_HISTORICAL_CAPTURE_RUNTIME_MANIFEST_SHA256:
        raise EvidenceError("historical capture runtime manifest is not the pinned V44 snapshot")
    source_bindings = _validate_frozen_sources(
        root, baseline_source_manifest_path.resolve(),
        baseline_manifest_digest,
        runtime_source_manifest_path.resolve(),
        geometry._sha(runtime_source_manifest_sha256, "runtime source manifest SHA-256"))
    historical_capture_runtime = _validate_historical_capture_runtime(
        root, historical_capture_runtime_manifest_path.resolve(),
        historical_runtime_digest)

    summary = text_extent._read(root, {
        "path": str(summary_path), "sha256": summary_sha256,
    })
    request = text_extent._read(root, summary["capture_request"])
    binding = geometry._object(request.get("binding"), "capture binding")
    evidence = _validate_annotation_free_inputs(
        root,
        (root / str(binding["path"])).resolve(), geometry._sha(binding["sha256"], "V3 binding"),
        (root / str(summary["capture_report"]["path"])).resolve(),
        geometry._sha(summary["capture_report"]["sha256"], "capture report"),
        (root / str(summary["capture_request"]["path"])).resolve(),
        geometry._sha(summary["capture_request"]["sha256"], "capture request"),
        candidate_path.resolve(), geometry._sha(candidate_sha256, "candidate"),
        evaluation_path.resolve(), geometry._sha(evaluation_sha256, "evaluation report"),
        historical_capture_runtime,
    )

    # Truth is inaccessible until the complete candidate/runtime evidence above authenticates.
    reports = [bridge.RuntimeReportEvidence(
        row["split"], root / row["manifest_path"], row["manifest_sha256"],
        root / row["report_path"], row["report_sha256"])
        for row in request["reports"]]
    prepared = bridge.prepare_text_extent_capture_request(
        root / binding["path"], binding["sha256"], reports,
        root / request["candidate"]["path"], request["candidate"]["sha256"],
        repository_root=root,
        capture_binary_root=historical_capture_runtime.backup_binary_root)
    generated_assemblies = geometry._validate_assemblies(
        root, prepared.request["assemblies"], "prepared historical backup assemblies")
    requested_assemblies = _validate_historical_capture_assemblies(
        root, request["assemblies"], historical_capture_runtime)
    if (
        [row["name"] for row in generated_assemblies]
        != [row["name"] for row in requested_assemblies]
        or [row["sha256"] for row in generated_assemblies]
        != [row["sha256"] for row in requested_assemblies]
    ):
        raise EvidenceError("prepared historical backup assemblies differ from the capture request")
    prepared_request = dict(prepared.request)
    prepared_request["assemblies"] = request["assemblies"]
    prepared = replace(prepared, request=prepared_request)
    if prepared.request != request:
        raise EvidenceError("capture request changed")
    inputs = bridge.load_text_extent_head_inputs(
        prepared, root / summary["capture_report"]["path"],
        summary["capture_report"]["sha256"], repository_root=root)
    preflight = text_extent._read(root, binding)
    train = text_extent._saved_train_truth(
        text_extent._read(root, preflight["train_text_truth"]), inputs.train)
    dev = text_extent._fixed_dev_truth(root, preflight)
    expected_dev = {truth.truth_id: truth for truth in inputs.dev.source_truths}
    if len(dev) != 183 or {truth.truth_id for truth in dev} != set(expected_dev):
        raise EvidenceError("fixed dev truth inventory changed")
    for truth in dev:
        expected = expected_dev[truth.truth_id]
        box = (truth.box.left, truth.box.top, truth.box.right, truth.box.bottom)
        if (truth.source_sha256 != expected.source_sha256
                or any(abs(a - b) > 1e-6 for a, b in zip(box, expected.source_box))):
            raise EvidenceError("fixed dev geometry changed")
    truths = {"train": train, "validation": dev}

    metrics: dict[str, Any] = {}
    for split, rows in truths.items():
        sources = {truth.source_sha256 for truth in rows}
        metrics[split] = metric._score_split(rows, {
            key: value for key, value in evidence.text_predictions_by_source.items()
            if key in sources
        })
    if sum(row["truth_region_count"] for row in metrics.values()) != 892:
        raise EvidenceError("participant-lane score did not retain all 892 source truths")
    raw_geometry, effective_geometry, recognized_geometry, failures = _geometry_metrics(
        evidence, truths)
    bar_sha, precision_bar, recall_bar = geometry._load_acceptance_bar(root)
    _, bar_payload = geometry._read_exact(
        root, geometry.ACCEPTANCE_BARS_PATH.as_posix(), bar_sha, "acceptance bars")
    bars = geometry._object(json.loads(bar_payload), "acceptance bars")
    tier = geometry._object(bars.get("tier1_reviewable_error"), "tier 1 acceptance bars")
    exact_bar = geometry._finite(tier.get("recognition_exact_match_minimum"), "exact bar")
    cer_bar = geometry._finite(tier.get("character_error_rate_maximum"), "CER bar")
    role_bar = geometry._finite(tier.get("role_accuracy_minimum"), "role bar")
    validation = metrics["validation"]
    validation_geometry = effective_geometry["validation"]

    result = {
        "schema": OUTPUT_SCHEMA,
        "status": "development_diagnostic_only_unapproved",
        "development_only": True,
        "synthetic_only": True,
        "private_data": False,
        "sealed_data": False,
        "optimizer_steps": 0,
        "production_approval": False,
        "release_eligible": False,
        "truth_isolation": {
            "runtime_received_truth": False,
            "all_runtime_candidate_and_assembly_evidence_authenticated_before_truth_access": True,
            "train_truth": "saved_preflight_text_and_geometry_no_regeneration",
            "dev_truth": "fixed_dev_renderer_authenticated_against_historical_rasters_and_geometry",
        },
        "inputs": {
            "binding": binding,
            "capture_request": summary["capture_request"],
            "capture_report": summary["capture_report"],
            "candidate": _descriptor(candidate_path, candidate_sha256, root),
            "evaluation_report": _descriptor(evaluation_path, evaluation_sha256, root),
            "scoring_adapter": _descriptor(Path(__file__), source_digest, root),
            "input_summary": _descriptor(summary_path, summary_sha256, root),
            "saved_train_truth": preflight["train_text_truth"],
            "historical_dev_binding": preflight["historical_dev"]["binding"],
            "source_bindings": source_bindings,
            "historical_capture_runtime": historical_capture_runtime.descriptor,
        },
        "composition": {
            "candidate_composition_version": CANDIDATE_COMPOSITION,
            "assembly_composition_version": ASSEMBLY_COMPOSITION,
            "raw_detector_regions_preserved_separately": True,
            "effective_region_membership_partitions_raw_inventory_on_completed_panels": True,
            "effective_region_geometry_independently_replayed_from_raw_regions_and_runtime_plot_bounds": True,
            "counts_by_split": evidence.assembly_counts,
        },
        "matching": {
            "coordinate_space": "source_original_pixels",
            "intersection_over_union_minimum": metric.MATCH_IOU_MINIMUM,
            "algorithm": "frozen_order_augmenting_path_maximum_cardinality",
            "text_role_and_alternatives_do_not_influence_pairing": True,
            "recognized_primary_text_used_exactly_without_normalization": True,
            "alternatives_used": False,
        },
        "metrics": metrics,
        "raw_detector_geometry": raw_geometry,
        "effective_region_geometry": effective_geometry,
        "successfully_recognized_effective_region_geometry": recognized_geometry,
        "recognition_failures": failures,
        "acceptance_bar_reference": {
            "path": geometry.ACCEPTANCE_BARS_PATH.as_posix(),
            "sha256": bar_sha,
            "recognition_exact_match_minimum": exact_bar,
            "character_error_rate_maximum": cer_bar,
            "role_accuracy_minimum": role_bar,
            "text_region_detection_precision_minimum": precision_bar,
            "text_region_detection_recall_minimum": recall_bar,
            "validation_recognition_exact_meets_bar":
                validation["recognition_exact_accuracy"] >= exact_bar,
            "validation_character_error_rate_meets_bar":
                validation["character_error_rate"] <= cer_bar,
            "validation_role_accuracy_meets_bar": validation["role_accuracy"] >= role_bar,
            "validation_effective_detection_precision_meets_bar":
                validation_geometry["precision"] >= precision_bar,
            "validation_effective_detection_recall_meets_bar":
                validation_geometry["recall"] >= recall_bar,
            "descriptive_only_until_candidate_outcome_is_closed": True,
            "scorer_does_not_select_or_approve_a_candidate": True,
        },
        "integrity": {
            "source_count": 23,
            "panel_count": 37,
            "full_source_truth_count": 892,
            "failed_panels_remain_in_full_source_denominator": True,
            "unmatched_truths_count_as_exact_and_role_failures_and_full_text_deletions": True,
            "unmatched_predictions_count_as_character_insertions": True,
        },
        "elapsed_milliseconds": (time.perf_counter() - started) * 1000.0,
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(result, stream, indent=2, sort_keys=True, allow_nan=False)
        stream.write("\n")
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--summary", type=Path, required=True)
    parser.add_argument("--summary-sha256", required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--candidate-sha256", required=True)
    parser.add_argument("--evaluation", type=Path, required=True)
    parser.add_argument("--evaluation-sha256", required=True)
    parser.add_argument("--baseline-source-manifest", type=Path, required=True)
    parser.add_argument("--baseline-source-manifest-sha256", required=True)
    parser.add_argument("--runtime-source-manifest", type=Path, required=True)
    parser.add_argument("--runtime-source-manifest-sha256", required=True)
    parser.add_argument("--historical-capture-runtime-manifest", type=Path, required=True)
    parser.add_argument("--historical-capture-runtime-manifest-sha256", required=True)
    parser.add_argument("--source-sha256", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = score(
        args.summary, args.summary_sha256,
        args.candidate, args.candidate_sha256,
        args.evaluation, args.evaluation_sha256,
        args.baseline_source_manifest, args.baseline_source_manifest_sha256,
        args.runtime_source_manifest, args.runtime_source_manifest_sha256,
        args.historical_capture_runtime_manifest,
        args.historical_capture_runtime_manifest_sha256,
        args.output, source_sha256=args.source_sha256)
    print(json.dumps({
        "status": result["status"],
        "validation": result["metrics"]["validation"],
    }, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
