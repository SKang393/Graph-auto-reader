# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Compose supplemental legend coverage with the frozen V44 DB-head inputs.

The six supplemental train sources are captured separately. The historical
20-source train and fixed development inputs remain byte-bound to their saved
request and capture. No renderer, detector inference, or optimizer runs here.
"""

from __future__ import annotations

from collections import Counter
from dataclasses import dataclass
from hashlib import sha256
import json
import math
from pathlib import Path
from types import SimpleNamespace
from typing import Any, Mapping, Sequence

from ml.ocr import production_tiled_inputs as tiled
from ml.ocr.official_bakeoff import production_head_inputs as head
from ml.ocr.official_bakeoff import text_extent_head_inputs as base
from ml.synthetic.io import canonical_json_bytes


REPOSITORY_ROOT = Path(__file__).resolve().parents[3]
PREFLIGHT_SCHEMA = "graphreader.train-legend-coverage-preflight.v1"
TRUTH_SCHEMA = "graphreader.ocr-train-legend-coverage-truth.v1"
REQUEST_SCHEMA = "graphreader.supplemental-official-head-tensor-capture-request.v1"
REPORT_SCHEMA = "graphreader.supplemental-official-head-tensor-capture-report.v1"
CAPTURE_SCOPE = "project-owned-synthetic-train-only-supplemental-model-free"
EXPECTED_SOURCES = 6
EXPECTED_PANELS = 6
EXPECTED_TRUTHS = 153
EXPECTED_ROLE_COUNTS = {
    "annotation": 12,
    "axis_title": 12,
    "condition_label": 12,
    "legend_text": 18,
    "participant": 6,
    "phase_heading": 12,
    "x_tick": 48,
    "y_tick": 33,
}
EXPECTED_COMBINED_SOURCES = 26
EXPECTED_COMBINED_PANELS = 34
EXPECTED_COMBINED_TRUTHS = 862
EXPECTED_OFFICIAL_CANDIDATE_SHA256 = (
    "154d40615bd54f5744e20bb5b4482b86e05f0e3d709c8ac8d794e8d3371a4e38"
)
EXPECTED_HISTORICAL_REQUEST_SHA256 = (
    "7d62e9e962d01448fd193887573b6a2ab882574eba41f67a543c444c6222c059"
)
EXPECTED_HISTORICAL_BINARY_MANIFEST_SHA256 = (
    "ed06006c6a4fcc078fc13817571a4fb58cd244add12731971ad8e7845be06aae"
)
EXPECTED_HISTORICAL_SOURCE_BINDING_SHA256 = (
    "b6572afe3bfcfffb4a27b0fca3e42bdcae8f83f77b9027a28cdb2f73e197bf87"
)
EXPECTED_HISTORICAL_CAPTURE_SOURCE_SHA256 = (
    "ada4110d386581e87de8f8693c86e045e41566270d601a836c79d41c86faf3a4"
)
EXPECTED_HISTORICAL_DLL_SHA256 = {
    "GraphReader.SyntheticRuntimeEvidence":
        "77d15df7f38a6249e59b009c5e420004eb59141634645c3dec4989d24b4edf70",
    "GraphReader.App":
        "53f1a3d0352b5c85c57ef21917e38d6e00d6f61d5546fb3976221c1f61e67578",
    "GraphReader.Ocr":
        "7a19d76db49863d393d1717cbbf42c14625dd69dc132247cea9dd420336cd701",
    "GraphReader.Inference":
        "201830c8a8eeba1af11350752209f3805d7b314f881252b94757d443da934930",
}
FAMILY_AXES = ("renderer", "font", "degradation", "template", "marker")


class LegendCoverageHeadInputError(head.ProductionHeadInputError):
    """The supplemental and historical head inputs do not compose exactly."""


@dataclass(frozen=True)
class SupplementalCapturePreparation:
    base_binding_sha256: str
    binding_sha256: str
    train: tiled.ProductionTiledSplit
    panels: tuple[head.CapturePanelInput, ...]
    request: dict[str, Any]


def build_supplemental_truth_document(
    preflight_path: Path,
    expected_preflight_sha256: str,
    *,
    repository_root: Path = REPOSITORY_ROOT,
) -> dict[str, Any]:
    """Read saved bound annotations once and emit deterministic all-role truth."""

    root = repository_root.resolve()
    preflight_path, preflight = _read_json(
        preflight_path, expected_preflight_sha256, root, "legend coverage preflight"
    )
    sources = _validate_preflight(preflight, root)
    truths: list[dict[str, Any]] = []
    inventory: list[dict[str, Any]] = []
    role_counts: Counter[str] = Counter()
    truth_ids: set[str] = set()
    text_ids: set[tuple[str, str]] = set()

    for source in sources:
        image = _descriptor(source.get("image"), "supplemental image")
        annotation_descriptor = _descriptor(
            source.get("annotation"), "supplemental annotation"
        )
        source_sha = _sha(image.get("sha256"), "supplemental source")
        _, annotation = _read_json(
            root / _string(annotation_descriptor, "path"),
            _sha(annotation_descriptor.get("sha256"), "supplemental annotation"),
            root,
            "supplemental annotation",
        )
        canvas = _object(annotation.get("canvas"), "annotation canvas")
        width = _positive_int(canvas.get("width"), "annotation width")
        height = _positive_int(canvas.get("height"), "annotation height")
        if _array(annotation.get("texts"), "top-level annotation texts"):
            raise LegendCoverageHeadInputError(
                "supplemental annotation text must be panel-scoped"
            )
        panels = _array(annotation.get("panels"), "annotation panels")
        if len(panels) != 1:
            raise LegendCoverageHeadInputError("supplemental source must contain one panel")
        panel = _object(panels[0], "annotation panel")
        panel_id = _string(panel, "panel_id")
        records = _array(panel.get("texts"), "panel text truth")
        source_count = 0
        for raw in records:
            record = _object(raw, "panel text truth")
            if record.get("visible") is not True:
                raise LegendCoverageHeadInputError("supplemental truth is not explicitly visible")
            text = _string(record, "text")
            if not text.strip():
                raise LegendCoverageHeadInputError("supplemental truth text is blank")
            if _string(record, "panel_id") != panel_id:
                raise LegendCoverageHeadInputError("supplemental truth panel identity changed")
            text_id = _string(record, "text_id")
            region_id = _string(record, "region_id")
            role = _string(record, "role")
            if text_id != region_id:
                raise LegendCoverageHeadInputError("supplemental text and region IDs differ")
            identity = (source_sha, text_id)
            truth_id = _hash_bytes(f"{source_sha}\n{text_id}".encode("utf-8"))
            if identity in text_ids or truth_id in truth_ids:
                raise LegendCoverageHeadInputError("supplemental truth identity repeats")
            text_ids.add(identity)
            truth_ids.add(truth_id)
            left, top, box_width, box_height = _xywh(
                record.get("rendered_pixel_box"), "rendered text box"
            )
            box = (left, top, left + box_width, top + box_height)
            tiled._validate_box(box, width, height, "supplemental source text")
            truths.append({
                "truth_id": truth_id,
                "source_id": source_sha,
                "source_sha256": source_sha,
                "text_id": text_id,
                "region_id": region_id,
                "panel_id": panel_id,
                "role": role,
                "text": text,
                "source_box_ltrb": list(box),
                "coordinate_space": "original_pixels",
            })
            role_counts[role] += 1
            source_count += 1
        inventory.append({
            "source_id": source_sha,
            "source_sha256": source_sha,
            "seed": _integer(source.get("seed"), "supplemental scene seed"),
            "family": _family(source),
            "width": width,
            "height": height,
            "text_truth_count": source_count,
            "image": dict(image),
            "annotation": dict(annotation_descriptor),
        })

    if len(truths) != EXPECTED_TRUTHS or dict(sorted(role_counts.items())) != EXPECTED_ROLE_COUNTS:
        raise LegendCoverageHeadInputError("supplemental all-role truth denominator changed")
    document = {
        "schema": TRUTH_SCHEMA,
        "status": "saved_all_role_truth_complete",
        "split": "train",
        "source_count": EXPECTED_SOURCES,
        "truth_count": EXPECTED_TRUTHS,
        "coordinate_space": "original_pixels",
        "synthetic_only": True,
        "private_data": False,
        "sealed_data": False,
        "renderer_execution": False,
        "model_inference": False,
        "optimizer_steps": 0,
        "production_approved": False,
        "preflight": {
            "path": _repository_path(preflight_path, root),
            "sha256": expected_preflight_sha256.lower(),
        },
        "role_counts": dict(sorted(role_counts.items())),
        "sources": inventory,
        "truths": truths,
    }
    _validate_truth(document, preflight, root)
    return document


def write_supplemental_truth(
    output_path: Path,
    preflight_path: Path,
    expected_preflight_sha256: str,
    *,
    repository_root: Path = REPOSITORY_ROOT,
) -> str:
    """Write the deterministic saved truth to a new artifact path."""

    root = repository_root.resolve()
    path = head._inside(root, output_path)
    payload = canonical_json_bytes(build_supplemental_truth_document(
        preflight_path, expected_preflight_sha256, repository_root=root
    ))
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("xb") as stream:
        stream.write(payload)
    return _hash_bytes(payload)


def prepare_historical_base_capture(
    preflight_path: Path,
    expected_preflight_sha256: str,
    runtime_reports: Sequence[base.RuntimeReportEvidence],
    candidate_path: Path,
    expected_candidate_sha256: str,
    stored_request_path: Path,
    expected_stored_request_sha256: str,
    historical_binary_root: Path,
    historical_capture_source_path: Path,
    historical_binary_manifest_path: Path,
    expected_historical_binary_manifest_sha256: str,
    historical_source_binding_path: Path,
    expected_historical_source_binding_sha256: str,
    *,
    repository_root: Path = REPOSITORY_ROOT,
) -> head.CapturePreparation:
    """Reconstruct V44 semantics, then retain the immutable saved request bytes."""

    root = repository_root.resolve()
    _require_pinned_sha(
        expected_candidate_sha256,
        EXPECTED_OFFICIAL_CANDIDATE_SHA256,
        "historical candidate",
    )
    _require_pinned_sha(
        expected_stored_request_sha256,
        EXPECTED_HISTORICAL_REQUEST_SHA256,
        "historical request",
    )
    _require_pinned_sha(
        expected_historical_binary_manifest_sha256,
        EXPECTED_HISTORICAL_BINARY_MANIFEST_SHA256,
        "historical binary manifest",
    )
    _require_pinned_sha(
        expected_historical_source_binding_sha256,
        EXPECTED_HISTORICAL_SOURCE_BINDING_SHA256,
        "historical source binding",
    )
    binary_manifest_path, binary_manifest = _read_array_json(
        historical_binary_manifest_path,
        expected_historical_binary_manifest_sha256,
        root,
        "historical capture binary manifest",
    )
    _validate_historical_binary_manifest(
        binary_manifest, binary_manifest_path, historical_binary_root, root
    )
    _, source_binding = _read_json(
        historical_source_binding_path,
        expected_historical_source_binding_sha256,
        root,
        "historical capture source binding",
    )
    _validate_historical_source_binding(
        source_binding, historical_capture_source_path, root
    )
    reconstructed = base.prepare_text_extent_capture_request(
        preflight_path,
        expected_preflight_sha256,
        runtime_reports,
        candidate_path,
        expected_candidate_sha256,
        repository_root=root,
        capture_binary_root=historical_binary_root,
        capture_source_path=historical_capture_source_path,
    )
    _, stored = _read_json(
        stored_request_path,
        expected_stored_request_sha256,
        root,
        "historical tensor capture request",
    )
    normalized = json.loads(json.dumps(reconstructed.request))
    normalized["capture_source"]["path"] = _string(
        _object(stored.get("capture_source"), "stored capture source"), "path"
    )
    stored_assemblies = _array(stored.get("assemblies"), "stored capture assemblies")
    generated_assemblies = _array(normalized.get("assemblies"), "generated capture assemblies")
    if len(stored_assemblies) != len(generated_assemblies):
        raise LegendCoverageHeadInputError("historical assembly inventory changed")
    stored_by_name = {
        _string(_object(item, "stored capture assembly"), "name"):
        _object(item, "stored capture assembly")
        for item in stored_assemblies
    }
    if len(stored_by_name) != len(stored_assemblies):
        raise LegendCoverageHeadInputError("historical assembly names repeat")
    for generated in generated_assemblies:
        row = _object(generated, "generated capture assembly")
        saved = stored_by_name.get(_string(row, "name"))
        if saved is None or saved.get("sha256") != row.get("sha256"):
            raise LegendCoverageHeadInputError("historical assembly bytes changed")
        row["path"] = _string(saved, "path")
    if normalized != stored:
        raise LegendCoverageHeadInputError(
            "historical capture reconstruction differs beyond preserved file paths"
        )
    return head.CapturePreparation(
        reconstructed.binding_sha256,
        reconstructed.tiled_inputs,
        reconstructed.panels,
        dict(stored),
    )


def prepare_legend_coverage_capture_request(
    base_preparation: head.CapturePreparation,
    preflight_path: Path,
    expected_preflight_sha256: str,
    truth_path: Path,
    expected_truth_sha256: str,
    manifest_path: Path,
    expected_manifest_sha256: str,
    runtime_report_path: Path,
    expected_runtime_report_sha256: str,
    candidate_path: Path,
    expected_candidate_sha256: str,
    *,
    repository_root: Path = REPOSITORY_ROOT,
    capture_binary_root: Path,
    capture_source_path: Path,
) -> SupplementalCapturePreparation:
    """Authenticate six runtime panels and build an annotation-free request."""

    root = repository_root.resolve()
    _require_pinned_sha(
        expected_candidate_sha256,
        EXPECTED_OFFICIAL_CANDIDATE_SHA256,
        "supplemental candidate",
    )
    _validate_base_preparation(base_preparation)
    preflight_path, preflight = _read_json(
        preflight_path, expected_preflight_sha256, root, "legend coverage preflight"
    )
    sources = _validate_preflight(preflight, root)
    truth_path, truth = _read_json(
        truth_path, expected_truth_sha256, root, "supplemental train text truth"
    )
    _validate_truth(truth, preflight, root)
    rebuilt_truth = build_supplemental_truth_document(
        preflight_path,
        expected_preflight_sha256,
        repository_root=root,
    )
    if truth != rebuilt_truth:
        raise LegendCoverageHeadInputError(
            "saved supplemental truth differs from authenticated annotations"
        )
    manifest_path, manifest = _read_json(
        manifest_path, expected_manifest_sha256, root, "supplemental runtime manifest"
    )
    manifest_sources = _validate_manifest(manifest, manifest_path, sources, root)
    candidate_path, candidate = _read_json(
        candidate_path, expected_candidate_sha256, root, "runtime candidate"
    )
    detector, detector_manifest_path, detector_manifest_sha = base._authenticate_candidate(
        candidate, root
    )
    report_path, report = _read_json(
        runtime_report_path,
        expected_runtime_report_sha256,
        root,
        "supplemental runtime report",
    )
    capture_panels, domains = _load_runtime_report(
        manifest_sources,
        manifest_path,
        expected_manifest_sha256,
        report,
        report_path,
        expected_runtime_report_sha256,
        candidate,
        expected_candidate_sha256,
        _integer(manifest.get("seed"), "runtime dataset seed"),
        root,
    )
    train = _build_supplemental_split(truth, manifest_sources, domains)
    _reject_base_collisions(base_preparation.tiled_inputs, train, capture_panels)

    source_path = head._inside(root, capture_source_path)
    binary_root = head._inside(root, capture_binary_root)
    assemblies = []
    for name in head.CAPTURE_ASSEMBLY_NAMES:
        path = head._inside(root, binary_root / f"{name}.dll")
        assemblies.append({
            "name": name,
            "path": head._repository_path(path, root),
            "sha256": _hash_file(path),
        })
    request = {
        "schema": REQUEST_SCHEMA,
        "scope": CAPTURE_SCOPE,
        "synthetic_only": True,
        "private_data": False,
        "sealed_data": False,
        "truth_included": False,
        "model_inference": False,
        "training_input_ready": False,
        "production_approved": False,
        "capture_source": {
            "path": head._repository_path(source_path, root),
            "sha256": _hash_file(source_path),
        },
        "assemblies": assemblies,
        "binding": {
            "path": head._repository_path(preflight_path, root),
            "sha256": expected_preflight_sha256.lower(),
        },
        "candidate": {
            "path": head._repository_path(candidate_path, root),
            "sha256": expected_candidate_sha256.lower(),
        },
        "detector": {
            "model_path": _string(detector, "model_path"),
            "model_id": _string(detector, "model_id"),
            "model_version": _string(detector, "model_version"),
            "model_sha256": _sha(detector.get("model_sha256"), "detector model"),
            "manifest_path": head._repository_path(detector_manifest_path, root),
            "manifest_sha256": detector_manifest_sha,
        },
        "native": {
            "path": _string(candidate, "native_path"),
            "sha256": _sha(candidate.get("native_sha256"), "native runtime"),
            "scope": _string(candidate, "native_scope"),
        },
        "license_inputs": candidate.get("license_inputs"),
        "maximum_side_length": head.MAXIMUM_SIDE_LENGTH,
        "dimension_multiple": head.DIMENSION_MULTIPLE,
        "detector_configuration_fingerprint": head.DETECTOR_CONFIGURATION_FINGERPRINT,
        "reports": [{
            "split": "train",
            "manifest_path": head._repository_path(manifest_path, root),
            "manifest_sha256": expected_manifest_sha256.lower(),
            "report_path": head._repository_path(report_path, root),
            "report_sha256": expected_runtime_report_sha256.lower(),
        }],
        "panels": [_capture_panel_record(item, root) for item in capture_panels],
    }
    encoded = json.dumps(request, sort_keys=True).lower()
    if any(token in encoded for token in ('"text"', "truth_id", "annotation")):
        raise LegendCoverageHeadInputError("supplemental capture request leaked truth")
    return SupplementalCapturePreparation(
        base_preparation.binding_sha256,
        expected_preflight_sha256.lower(),
        train,
        tuple(capture_panels),
        request,
    )


def load_legend_coverage_head_inputs(
    base_inputs: head.ProductionHeadInputs,
    preparation: SupplementalCapturePreparation,
    capture_report_path: Path,
    expected_capture_report_sha256: str,
    *,
    repository_root: Path = REPOSITORY_ROOT,
) -> head.ProductionHeadInputs:
    """Load six captured tensors and merge train only; return base dev unchanged."""

    root = repository_root.resolve()
    if base_inputs.binding_sha256 != preparation.base_binding_sha256:
        raise LegendCoverageHeadInputError("historical base input binding changed")
    _validate_base_split(base_inputs.train, base_inputs.dev)
    report_path, report = _read_json(
        capture_report_path,
        expected_capture_report_sha256,
        root,
        "supplemental tensor capture report",
    )
    request_descriptor = _object(report.get("request"), "supplemental capture request")
    _, captured_request = _read_json(
        root / _string(request_descriptor, "path"),
        _sha(request_descriptor.get("sha256"), "supplemental capture request"),
        root,
        "supplemental capture request",
    )
    if captured_request != preparation.request:
        raise LegendCoverageHeadInputError("captured supplemental request changed")
    _validate_capture_report(report, preparation)
    records = _array(report.get("panels"), "supplemental captured panels")
    by_id: dict[str, Mapping[str, Any]] = {}
    for raw in records:
        record = _object(raw, "supplemental captured panel")
        panel_id = _string(record, "panel_id")
        if panel_id in by_id:
            raise LegendCoverageHeadInputError("supplemental capture repeats a panel")
        by_id[panel_id] = record
    requested = {item.panel_id: item for item in preparation.panels}
    if set(by_id) != set(requested):
        raise LegendCoverageHeadInputError("supplemental captured panel inventory changed")
    supplemental = head._load_split(
        preparation.train, by_id, requested, report_path, root
    )
    return _compose_inputs(
        base_inputs,
        preparation,
        supplemental,
        expected_capture_report_sha256.lower(),
    )


def write_supplemental_capture_request(
    output_path: Path,
    preparation: SupplementalCapturePreparation,
    *,
    repository_root: Path = REPOSITORY_ROOT,
) -> str:
    """Write an annotation-free supplemental request to a new artifact path."""

    root = repository_root.resolve()
    path = head._inside(root, output_path)
    payload = canonical_json_bytes(preparation.request)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("xb") as stream:
        stream.write(payload)
    return _hash_bytes(payload)


def _validate_preflight(
    value: Mapping[str, Any], root: Path
) -> tuple[Mapping[str, Any], ...]:
    if (
        value.get("schema") != PREFLIGHT_SCHEMA
        or value.get("status") != "supplemental_train_legend_coverage_preflight_execution_complete"
        or value.get("execution_complete") is not True
        or value.get("coverage_ready") is not True
        or value.get("train_only") is not True
        or value.get("synthetic_only") is not True
        or value.get("dev_truth_rows_parsed") != 0
        or value.get("dev_pixels_read") != 0
        or value.get("private_reads") != 0
        or value.get("sealed_reads") != 0
        or value.get("model_loads") != 0
        or value.get("model_inference_runs") != 0
        or value.get("optimizer_steps") != 0
        or value.get("ocr_revision_opened") is not False
        or value.get("training_authorized") is not False
        or value.get("production_approved") is not False
    ):
        raise LegendCoverageHeadInputError("supplemental preflight scope changed")
    coverage = _object(value.get("coverage"), "supplemental coverage")
    if (
        coverage.get("source_count") != EXPECTED_SOURCES
        or coverage.get("panel_count") != EXPECTED_PANELS
        or coverage.get("legend_truth_count") != 18
        or coverage.get("inside_plot_legend_truth_count") != 18
        or coverage.get("new_raster_hashes_disjoint_from_historical_train") is not True
        or coverage.get("new_raster_hashes_disjoint_from_fixed_dev") is not True
    ):
        raise LegendCoverageHeadInputError("supplemental coverage denominator changed")
    sources = tuple(_object(item, "supplemental source") for item in _array(
        value.get("sources"), "supplemental sources"
    ))
    if len(sources) != EXPECTED_SOURCES:
        raise LegendCoverageHeadInputError("supplemental source denominator changed")
    seen: set[str] = set()
    for source in sources:
        if source.get("index") != len(seen) or source.get("legend_truth_count") != 3:
            raise LegendCoverageHeadInputError("supplemental source order or legend count changed")
        for key in ("image", "scene", "annotation"):
            descriptor = _descriptor(source.get(key), f"supplemental {key}")
            path = head._inside(root, root / _string(descriptor, "path"))
            digest = _sha(descriptor.get("sha256"), f"supplemental {key}")
            if _hash_file(path) != digest:
                raise LegendCoverageHeadInputError(f"supplemental {key} bytes changed")
        source_sha = _sha(_descriptor(source.get("image"), "supplemental image").get("sha256"), "source")
        if source_sha in seen:
            raise LegendCoverageHeadInputError("supplemental source identity repeats")
        seen.add(source_sha)
    for raw in _array(value.get("generator_sources"), "generator source bindings"):
        descriptor = _object(raw, "generator source binding")
        path = head._inside(root, root / _string(descriptor, "path"))
        if _hash_file(path) != _sha(descriptor.get("sha256"), "generator source"):
            raise LegendCoverageHeadInputError("generator source binding changed")
    return sources


def _validate_truth(
    truth: Mapping[str, Any], preflight: Mapping[str, Any], root: Path
) -> None:
    if (
        truth.get("schema") != TRUTH_SCHEMA
        or truth.get("status") != "saved_all_role_truth_complete"
        or truth.get("split") != "train"
        or truth.get("source_count") != EXPECTED_SOURCES
        or truth.get("truth_count") != EXPECTED_TRUTHS
        or truth.get("coordinate_space") != "original_pixels"
        or truth.get("synthetic_only") is not True
        or truth.get("private_data") is not False
        or truth.get("sealed_data") is not False
        or truth.get("renderer_execution") is not False
        or truth.get("model_inference") is not False
        or truth.get("optimizer_steps") != 0
        or truth.get("production_approved") is not False
        or truth.get("role_counts") != EXPECTED_ROLE_COUNTS
    ):
        raise LegendCoverageHeadInputError("supplemental truth scope or denominator changed")
    preflight_descriptor = _descriptor(truth.get("preflight"), "truth preflight")
    expected_preflight = _hash_bytes(canonical_json_bytes(preflight))
    if _sha(preflight_descriptor.get("sha256"), "truth preflight") != expected_preflight:
        raise LegendCoverageHeadInputError("supplemental truth preflight binding changed")
    if len(_array(truth.get("truths"), "supplemental truths")) != EXPECTED_TRUTHS:
        raise LegendCoverageHeadInputError("supplemental truth rows changed")


def _validate_manifest(
    manifest: Mapping[str, Any],
    manifest_path: Path,
    preflight_sources: Sequence[Mapping[str, Any]],
    root: Path,
) -> dict[str, dict[str, Any]]:
    if (
        set(manifest) != {
            "schema", "source", "preset", "seed", "split",
            "contains_truth", "contains_precomputed_masks", "images",
        }
        or manifest.get("schema") != "graphreader.synthetic-runtime-raster-inputs.v1"
        or manifest.get("source") != "project-owned-synthetic-five-axis-family-v1"
        or manifest.get("preset") != "supplemental-train-legend-coverage-v1"
        or manifest.get("split") != "train"
        or manifest.get("contains_truth") is not False
        or manifest.get("contains_precomputed_masks") is not False
    ):
        raise LegendCoverageHeadInputError("supplemental runtime manifest scope changed")
    expected = {
        _sha(_descriptor(item.get("image"), "preflight image").get("sha256"), "source"): item
        for item in preflight_sources
    }
    output: dict[str, dict[str, Any]] = {}
    for raw in _array(manifest.get("images"), "supplemental manifest images"):
        image = dict(_object(raw, "supplemental manifest image"))
        if set(image) != {"family", "height", "image", "image_sha256", "seed", "split", "width"}:
            raise LegendCoverageHeadInputError("supplemental manifest image shape changed")
        source_sha = _sha(image.get("image_sha256"), "manifest source")
        source = expected.get(source_sha)
        path = head._inside(root, manifest_path.parent / _string(image, "image"))
        if (
            source is None
            or source_sha in output
            or image.get("split") != "train"
            or image.get("seed") != source.get("seed")
            or image.get("family") != _family(source)
            or _hash_file(path) != source_sha
            or _positive_int(image.get("width"), "manifest width") <= 0
            or _positive_int(image.get("height"), "manifest height") <= 0
        ):
            raise LegendCoverageHeadInputError("supplemental manifest source changed")
        output[source_sha] = image
    if set(output) != set(expected):
        raise LegendCoverageHeadInputError("supplemental manifest source inventory changed")
    return output


def _load_runtime_report(
    manifest_sources: Mapping[str, Mapping[str, Any]],
    manifest_path: Path,
    manifest_sha: str,
    report: Mapping[str, Any],
    report_path: Path,
    report_sha: str,
    candidate: Mapping[str, Any],
    candidate_sha: str,
    dataset_seed: int,
    root: Path,
) -> tuple[list[head.CapturePanelInput], list[Any]]:
    if (
        report.get("schema") != "graphreader.synthetic-runtime-seed-evidence.v2"
        or report.get("scope") != "local-synthetic-seed-diagnostic"
        or report.get("production_approved") is not False
        or report.get("training_input_ready") is not False
        or report.get("input_manifest_sha256") != manifest_sha.lower()
        or report.get("candidate_sha256") != candidate_sha.lower()
        or report.get("native_scope") != candidate.get("native_scope")
        or report.get("native_sha256") != candidate.get("native_sha256")
        or report.get("count") != EXPECTED_SOURCES
        or report.get("completed") != EXPECTED_SOURCES
        or report.get("failed") != 0
        or report.get("panel_count") != EXPECTED_PANELS
        or report.get("completed_panels") != EXPECTED_PANELS
        or report.get("failed_panels") != 0
    ):
        raise LegendCoverageHeadInputError("supplemental runtime report changed")
    panels: list[head.CapturePanelInput] = []
    domains: list[Any] = []
    seen_sources: set[str] = set()
    seen_panels: set[str] = set()
    seen_panel_hashes: set[str] = set()
    for raw_case in _array(report.get("cases"), "supplemental runtime cases"):
        case = _object(raw_case, "supplemental runtime case")
        source_sha = _sha(case.get("image_sha256"), "runtime source")
        source = manifest_sources.get(source_sha)
        case_panels = _array(case.get("panels"), "runtime source panels")
        if (
            source is None
            or source_sha in seen_sources
            or case.get("status") != "panels-completed"
            or case.get("width") != source.get("width")
            or case.get("height") != source.get("height")
            or len(case_panels) != 1
        ):
            raise LegendCoverageHeadInputError("supplemental runtime source changed")
        seen_sources.add(source_sha)
        panel = _object(case_panels[0], "supplemental runtime panel")
        panel_id = _string(panel, "panel_id")
        panel_sha = _sha(panel.get("image_sha256"), "runtime panel")
        width = _positive_int(panel.get("width"), "runtime panel width")
        height = _positive_int(panel.get("height"), "runtime panel height")
        crop_object = _object(panel.get("crop"), "runtime crop")
        crop = tuple(_integer(crop_object.get(key), f"runtime crop {key}") for key in (
            "x", "y", "width", "height"
        ))
        if (
            panel.get("status") != "seed-completed"
            or panel.get("source_image_sha256") != source_sha
            or panel_id in seen_panels
            or panel_sha in seen_panel_hashes
            or crop[2:] != (width, height)
            or panel.get("source_width") != source.get("width")
            or panel.get("source_height") != source.get("height")
            or crop[0] < 0
            or crop[1] < 0
            or crop[0] + crop[2] > source["width"]
            or crop[1] + crop[3] > source["height"]
        ):
            raise LegendCoverageHeadInputError("supplemental runtime panel changed")
        seen_panels.add(panel_id)
        seen_panel_hashes.add(panel_sha)
        requested = base._box_object(panel.get("requested_crop"), "runtime requested crop")
        source_to_panel = base._matrix(panel.get("source_to_panel_matrix"), "source-to-panel")
        panel_to_source = base._matrix(panel.get("panel_to_source_matrix"), "panel-to-source")
        expected_forward = (
            1.0, 0.0, -float(crop[0]), 0.0, 1.0, -float(crop[1]), 0.0, 0.0, 1.0
        )
        expected_inverse = (
            1.0, 0.0, float(crop[0]), 0.0, 1.0, float(crop[1]), 0.0, 0.0, 1.0
        )
        if (
            source_to_panel != expected_forward
            or panel_to_source != expected_inverse
            or requested[0] < crop[0]
            or requested[1] < crop[1]
            or requested[0] + requested[2] > crop[0] + crop[2]
            or requested[1] + requested[3] > crop[1] + crop[3]
        ):
            raise LegendCoverageHeadInputError("supplemental runtime transform changed")
        png = _object(panel.get("panel_png"), "runtime panel PNG")
        if _string(png, "file") != "panel.png":
            raise LegendCoverageHeadInputError("runtime panel PNG name changed")
        png_path = head._inside(
            root, report_path.parent / source_sha / panel_id / _string(png, "file")
        )
        png_sha = _sha(png.get("sha256"), "runtime panel PNG")
        byte_count = _positive_int(png.get("byte_count"), "runtime panel PNG bytes")
        encoded = base._read_exact(png_path, png_sha, byte_count, "runtime panel PNG")
        if png_sha != panel_sha:
            raise LegendCoverageHeadInputError("runtime panel PNG identity changed")
        gray, bgr = head._decode_production_pixels(encoded, width, height)
        diagnostic = _object(panel.get("ocr_proposal_diagnostic"), "OCR diagnostic")
        gray_sha = _sha(diagnostic.get("unmasked_input_sha256"), "runtime Gray8")
        if _hash_bytes(gray.tobytes(order="C")) != gray_sha:
            raise LegendCoverageHeadInputError("runtime Gray8 identity changed")
        bgr_sha = _hash_bytes(bgr.tobytes(order="C"))
        runtime = SimpleNamespace(
            split="train",
            dataset_seed=dataset_seed,
            family=str(source["family"]),
            scene_seed=_integer(source["seed"], "runtime scene seed"),
            source_sha256=source_sha,
            panel_id=panel_id,
            panel_sha256=panel_sha,
            width=width,
            height=height,
            crop=crop,
            requested_crop=requested,
            gray8=gray,
        )
        domains.append(SimpleNamespace(
            runtime_input=runtime,
            source_width=source["width"],
            source_height=source["height"],
            source_to_panel_matrix=source_to_panel,
            panel_to_source_matrix=panel_to_source,
        ))
        panels.append(head.CapturePanelInput(
            "train", source_sha, panel_id, panel_sha, width, height, crop,
            report_path, report_sha.lower(), png_path, png_sha, byte_count,
            gray_sha, bgr_sha,
        ))
    if seen_sources != set(manifest_sources):
        raise LegendCoverageHeadInputError("supplemental runtime source inventory changed")
    return panels, domains


def _build_supplemental_split(
    truth: Mapping[str, Any],
    sources: Mapping[str, Mapping[str, Any]],
    domains: Sequence[Any],
) -> tiled.ProductionTiledSplit:
    by_source = {item.runtime_input.source_sha256: item for item in domains}
    projections: dict[str, list[tiled.PanelTextProjection]] = {
        item.runtime_input.panel_id: [] for item in domains
    }
    truths: list[tiled.SourceTextTruth] = []
    per_source: Counter[str] = Counter()
    for raw in _array(truth.get("truths"), "supplemental truths"):
        item = _object(raw, "supplemental truth")
        source_sha = _sha(item.get("source_sha256"), "truth source")
        domain = by_source.get(source_sha)
        if domain is None:
            raise LegendCoverageHeadInputError("supplemental truth source is unbound")
        truth_id = _sha(item.get("truth_id"), "truth ID")
        text_id = _string(item, "text_id")
        box = base._box(item.get("source_box_ltrb"), "truth source box")
        projection = tiled._project_truth(truth_id, text_id, box, domain)
        if projection is None or projection.status != "full":
            raise LegendCoverageHeadInputError("supplemental truth was discarded or clipped")
        projections[projection.panel_id].append(projection)
        runtime = domain.runtime_input
        truths.append(tiled.SourceTextTruth(
            truth_id,
            "train",
            runtime.dataset_seed,
            runtime.family,
            runtime.scene_seed,
            source_sha,
            text_id,
            _string(item, "role"),
            box,
            (projection,),
            tiled._projection_status(box, (projection,)),
        ))
        per_source[source_sha] += 1
    truth_sources = {
        _sha(item.get("source_sha256"), "truth inventory source"):
        _positive_int(item.get("text_truth_count"), "source truth count")
        for item in _array(truth.get("sources"), "truth source inventory")
    }
    if set(truth_sources) != set(sources) or dict(per_source) != truth_sources:
        raise LegendCoverageHeadInputError("supplemental per-source truth counts changed")
    panels = tuple(
        base._tiled_panel(domain, tuple(projections[domain.runtime_input.panel_id]))
        for domain in sorted(domains, key=lambda value: value.runtime_input.panel_id)
    )
    result = base._split("train", truths, panels, EXPECTED_SOURCES)
    if (
        result.panel_count != EXPECTED_PANELS
        or result.full_source_truth_count != EXPECTED_TRUTHS
        or result.projected_source_truth_count != EXPECTED_TRUTHS
        or result.outside_runtime_crop_truth_count != 0
        or result.partial_source_truth_count != 0
        or result.overlapping_source_truth_count != 0
    ):
        raise LegendCoverageHeadInputError("supplemental projected truth denominator changed")
    return result


def _validate_base_preparation(value: head.CapturePreparation) -> None:
    _validate_base_split(value.tiled_inputs.train, value.tiled_inputs.dev)
    if len(value.panels) != base.EXPECTED_TRAIN_PANELS + base.EXPECTED_DEV_PANELS:
        raise LegendCoverageHeadInputError("historical preparation panel count changed")


def _validate_base_split(
    train: head.ProductionHeadSplit | tiled.ProductionTiledSplit,
    dev: head.ProductionHeadSplit | tiled.ProductionTiledSplit,
) -> None:
    if (
        train.source_count != base.EXPECTED_TRAIN_SOURCES
        or train.panel_count != base.EXPECTED_TRAIN_PANELS
        or train.full_source_truth_count != base.EXPECTED_TRAIN_TRUTHS
        or dev.source_count != base.EXPECTED_DEV_SOURCES
        or dev.panel_count != base.EXPECTED_DEV_PANELS
        or dev.full_source_truth_count != base.EXPECTED_DEV_TRUTHS
    ):
        raise LegendCoverageHeadInputError("historical train/dev denominator changed")


def _reject_base_collisions(
    base_inputs: tiled.ProductionTiledInputs,
    supplemental: tiled.ProductionTiledSplit,
    capture_panels: Sequence[head.CapturePanelInput],
) -> None:
    base_truths = (*base_inputs.train.source_truths, *base_inputs.dev.source_truths)
    base_panels = (*base_inputs.train.panels, *base_inputs.dev.panels)
    if {item.source_sha256 for item in supplemental.source_truths} & {
        item.source_sha256 for item in base_truths
    }:
        raise LegendCoverageHeadInputError("supplemental source collides with historical data")
    if {item.truth_id for item in supplemental.source_truths} & {
        item.truth_id for item in base_truths
    }:
        raise LegendCoverageHeadInputError("supplemental truth ID collides with historical data")
    if {item.panel_id for item in supplemental.panels} & {item.panel_id for item in base_panels}:
        raise LegendCoverageHeadInputError("supplemental panel ID collides with historical data")
    if {item.panel_sha256 for item in supplemental.panels} & {
        item.panel_sha256 for item in base_panels
    }:
        raise LegendCoverageHeadInputError("supplemental panel hash collides with historical data")
    if len({item.reconstructed_bgr_sha256 for item in capture_panels}) != len(capture_panels):
        raise LegendCoverageHeadInputError("supplemental BGR identity repeats")


def _validate_capture_report(
    report: Mapping[str, Any], preparation: SupplementalCapturePreparation
) -> None:
    expected = {
        "schema": REPORT_SCHEMA,
        "scope": CAPTURE_SCOPE,
        "synthetic_only": True,
        "private_data": False,
        "sealed_data": False,
        "truth_used_by_capture": False,
        "model_inference": False,
        "training_input_ready": False,
        "production_approved": False,
        "binding_sha256": preparation.binding_sha256,
        "maximum_side_length": head.MAXIMUM_SIDE_LENGTH,
        "dimension_multiple": head.DIMENSION_MULTIPLE,
        "panel_count": EXPECTED_PANELS,
        "failed_panel_count": 0,
        "candidate_sha256": preparation.request["candidate"]["sha256"],
        "detector_model_sha256": preparation.request["detector"]["model_sha256"],
        "detector_manifest_sha256": preparation.request["detector"]["manifest_sha256"],
        "native_sha256": preparation.request["native"]["sha256"],
        "capture_source_sha256": preparation.request["capture_source"]["sha256"],
        "detector_configuration_fingerprint": head.DETECTOR_CONFIGURATION_FINGERPRINT,
        "assemblies": preparation.request["assemblies"],
    }
    if any(report.get(key) != value for key, value in expected.items()):
        raise LegendCoverageHeadInputError("supplemental capture report header changed")
    if len(_array(report.get("panels"), "supplemental captured panels")) != EXPECTED_PANELS:
        raise LegendCoverageHeadInputError("supplemental capture panel denominator changed")


def _merge_train(
    historical: head.ProductionHeadSplit,
    supplemental: head.ProductionHeadSplit,
) -> head.ProductionHeadSplit:
    truth_ids = [item.truth_id for item in (*historical.source_truths, *supplemental.source_truths)]
    panel_ids = [item.panel_id for item in (*historical.panels, *supplemental.panels)]
    panel_hashes = [item.panel_sha256 for item in (*historical.panels, *supplemental.panels)]
    tensor_hashes = [item.tensor_sha256 for item in (*historical.panels, *supplemental.panels)]
    bgr_hashes = [item.reconstructed_bgr_sha256 for item in (*historical.panels, *supplemental.panels)]
    for values, label in (
        (truth_ids, "truth ID"),
        (panel_ids, "panel ID"),
        (panel_hashes, "panel hash"),
        (tensor_hashes, "tensor hash"),
        (bgr_hashes, "BGR hash"),
    ):
        if len(set(values)) != len(values):
            raise LegendCoverageHeadInputError(f"combined train {label} collides")
    truths = tuple((*historical.source_truths, *supplemental.source_truths))
    panels = tuple(sorted((*historical.panels, *supplemental.panels), key=lambda item: item.panel_id))
    result = head.ProductionHeadSplit(
        "train",
        len({item.source_sha256 for item in truths}),
        len(panels),
        len(truths),
        sum(bool(item.projections) for item in truths),
        sum(not item.projections for item in truths),
        sum(any(projection.status == "partial" for projection in item.projections) for item in truths),
        sum(len(item.projections) > 1 for item in truths),
        historical.degenerate_supervised_region_count
        + supplemental.degenerate_supervised_region_count,
        historical.degenerate_source_truth_count
        + supplemental.degenerate_source_truth_count,
        truths,
        panels,
    )
    if (
        result.source_count != EXPECTED_COMBINED_SOURCES
        or result.panel_count != EXPECTED_COMBINED_PANELS
        or result.full_source_truth_count != EXPECTED_COMBINED_TRUTHS
    ):
        raise LegendCoverageHeadInputError("combined train denominator changed")
    return result


def _compose_inputs(
    base_inputs: head.ProductionHeadInputs,
    preparation: SupplementalCapturePreparation,
    supplemental: head.ProductionHeadSplit,
    supplemental_capture_sha256: str,
) -> head.ProductionHeadInputs:
    """Compose train while preserving the authenticated development object."""

    return head.ProductionHeadInputs(
        _combined_identity(base_inputs.binding_sha256, preparation.binding_sha256),
        _combined_identity(
            base_inputs.capture_report_sha256, supplemental_capture_sha256
        ),
        _merge_train(base_inputs.train, supplemental),
        base_inputs.dev,
    )


def _capture_panel_record(item: head.CapturePanelInput, root: Path) -> dict[str, Any]:
    return {
        "split": item.split,
        "source_sha256": item.source_sha256,
        "panel_id": item.panel_id,
        "panel_sha256": item.panel_sha256,
        "width": item.width,
        "height": item.height,
        "crop": list(item.crop),
        "report_path": head._repository_path(item.report_path, root),
        "report_sha256": item.report_sha256,
        "panel_png": {
            "path": head._repository_path(item.panel_png_path, root),
            "sha256": item.panel_png_sha256,
            "byte_count": item.panel_png_byte_count,
        },
        "recorded_unmasked_gray_sha256": item.recorded_unmasked_gray_sha256,
        "reconstructed_bgr_sha256": item.reconstructed_bgr_sha256,
        "bgr_identity_kind": "reconstructed_from_authenticated_panel_png",
    }


def _family(source: Mapping[str, Any]) -> str:
    families = _object(source.get("families"), "supplemental families")
    if set(families) != set(FAMILY_AXES):
        raise LegendCoverageHeadInputError("supplemental family axes changed")
    return "|".join(f"{axis}={_string(families, axis)}" for axis in FAMILY_AXES)


def _xywh(value: Any, label: str) -> tuple[float, float, float, float]:
    if not isinstance(value, list) or len(value) != 4:
        raise LegendCoverageHeadInputError(f"{label} must contain four values")
    values = tuple(float(item) for item in value)
    if not all(math.isfinite(item) for item in values) or values[2] <= 0 or values[3] <= 0:
        raise LegendCoverageHeadInputError(f"{label} is invalid")
    return values


def _combined_identity(left: str, right: str) -> str:
    return _hash_bytes(f"{left.lower()}\n{right.lower()}".encode("ascii"))


def _validate_historical_binary_manifest(
    rows: Sequence[Any], manifest_path: Path, binary_root: Path, root: Path
) -> None:
    if len(rows) != 8:
        raise LegendCoverageHeadInputError("historical binary backup inventory changed")
    expected_root = head._inside(root, binary_root)
    seen_originals: set[str] = set()
    dll_hashes: dict[str, str] = {}
    for raw in rows:
        row = _object(raw, "historical binary backup")
        if set(row) != {"sha256", "backup_path", "original_path"}:
            raise LegendCoverageHeadInputError("historical binary backup shape changed")
        original = _string(row, "original_path").replace("\\", "/")
        if original in seen_originals:
            raise LegendCoverageHeadInputError("historical binary backup repeats")
        seen_originals.add(original)
        backup = head._inside(root, root / _string(row, "backup_path"))
        digest = _sha(row.get("sha256"), "historical binary backup")
        if backup.parent != manifest_path.parent or _hash_file(backup) != digest:
            raise LegendCoverageHeadInputError("historical binary backup bytes changed")
        if backup.suffix.casefold() == ".dll" and backup.name.startswith("GraphReader."):
            if backup.parent != expected_root or not original.endswith("/" + backup.name):
                raise LegendCoverageHeadInputError("historical capture DLL mapping changed")
            dll_hashes[backup.stem] = digest
    if dll_hashes != EXPECTED_HISTORICAL_DLL_SHA256:
        raise LegendCoverageHeadInputError("historical capture DLL inventory changed")


def _validate_historical_source_binding(
    binding: Mapping[str, Any], capture_source_path: Path, root: Path
) -> None:
    if set(binding) != {"original", "snapshot"}:
        raise LegendCoverageHeadInputError("historical capture source binding shape changed")
    original = _descriptor(binding.get("original"), "historical original capture source")
    snapshot = head._inside(root, root / _string(binding, "snapshot"))
    expected_snapshot = head._inside(root, capture_source_path)
    if (
        snapshot != expected_snapshot
        or _hash_file(snapshot)
        != _sha(original.get("sha256"), "historical capture source")
        or _sha(original.get("sha256"), "historical capture source")
        != EXPECTED_HISTORICAL_CAPTURE_SOURCE_SHA256
        or _string(original, "path")
        != "tools/GraphReader.SyntheticRuntimeEvidence/OfficialHeadTensorCapture.cs"
    ):
        raise LegendCoverageHeadInputError("historical capture source binding changed")


def _read_json(
    path: Path, expected_sha: str, root: Path, label: str
) -> tuple[Path, Mapping[str, Any]]:
    try:
        return base._read_json(path, expected_sha, root, label)
    except base.TextExtentHeadInputError as exception:
        raise LegendCoverageHeadInputError(str(exception)) from exception


def _read_array_json(
    path: Path, expected_sha: str, root: Path, label: str
) -> tuple[Path, Sequence[Any]]:
    resolved = head._inside(root, path)
    payload = resolved.read_bytes()
    if _hash_bytes(payload) != _sha(expected_sha, label):
        raise LegendCoverageHeadInputError(f"{label} checksum changed")
    try:
        value = json.loads(payload)
    except (UnicodeDecodeError, json.JSONDecodeError) as exception:
        raise LegendCoverageHeadInputError(f"{label} is invalid JSON") from exception
    return resolved, _array(value, label)


def _descriptor(value: Any, label: str) -> Mapping[str, Any]:
    result = _object(value, label)
    if set(result) != {"path", "sha256"}:
        raise LegendCoverageHeadInputError(f"{label} descriptor changed")
    return result


def _object(value: Any, label: str) -> Mapping[str, Any]:
    if not isinstance(value, dict):
        raise LegendCoverageHeadInputError(f"{label} must be an object")
    return value


def _array(value: Any, label: str) -> Sequence[Any]:
    if not isinstance(value, list):
        raise LegendCoverageHeadInputError(f"{label} must be an array")
    return value


def _string(value: Mapping[str, Any], key: str) -> str:
    result = value.get(key)
    if not isinstance(result, str) or not result:
        raise LegendCoverageHeadInputError(f"{key} must be a nonempty string")
    return result


def _sha(value: Any, label: str) -> str:
    if not isinstance(value, str) or len(value) != 64 or any(
        character not in "0123456789abcdefABCDEF" for character in value
    ):
        raise LegendCoverageHeadInputError(f"{label} must be a SHA-256")
    return value.lower()


def _require_pinned_sha(value: Any, expected: str, label: str) -> None:
    if _sha(value, label) != expected:
        raise LegendCoverageHeadInputError(f"{label} is not the frozen artifact")


def _integer(value: Any, label: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int):
        raise LegendCoverageHeadInputError(f"{label} must be an integer")
    return value


def _positive_int(value: Any, label: str) -> int:
    result = _integer(value, label)
    if result <= 0:
        raise LegendCoverageHeadInputError(f"{label} must be positive")
    return result


def _hash_bytes(payload: bytes) -> str:
    return sha256(payload).hexdigest()


def _hash_file(path: Path) -> str:
    return _hash_bytes(path.read_bytes())


def _repository_path(path: Path, root: Path) -> str:
    return head._inside(root, path).relative_to(root).as_posix()


__all__ = [
    "CAPTURE_SCOPE",
    "LegendCoverageHeadInputError",
    "REPORT_SCHEMA",
    "REQUEST_SCHEMA",
    "SupplementalCapturePreparation",
    "TRUTH_SCHEMA",
    "build_supplemental_truth_document",
    "load_legend_coverage_head_inputs",
    "prepare_historical_base_capture",
    "prepare_legend_coverage_capture_request",
    "write_supplemental_capture_request",
    "write_supplemental_truth",
]
