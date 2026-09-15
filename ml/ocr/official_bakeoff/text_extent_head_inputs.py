# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Authenticated production-head inputs for the train-only text-extent experiment.

The changed train renderer writes exact text truth once.  This bridge never
reruns that renderer.  It joins the saved truth to current application panel
reports only after the preflight, sources, reports, detector, native runtime,
capture source, and executing assemblies have authenticated.  The C# capture
request contains pixels and identities only, never text or annotations.
"""

from __future__ import annotations

from dataclasses import dataclass
from hashlib import sha256
import json
from pathlib import Path
from types import SimpleNamespace
from typing import Any, Mapping, Sequence

import numpy as np

from ml.ocr import production_tiled_inputs
from ml.ocr.official_bakeoff import production_head_inputs as head


REPOSITORY_ROOT = Path(__file__).resolve().parents[3]
PREFLIGHT_SCHEMA = "graphreader.ocr-train-text-extent-preflight.v1"
TRAIN_TRUTH_SCHEMA = "graphreader.ocr-train-text-extent-truth.v1"
ORACLE_REQUEST_SCHEMA = "graphreader.db-target-oracle-request.v1"
EXPECTED_TRAIN_SOURCES = 20
EXPECTED_TRAIN_REPORTS = 5
EXPECTED_TRAIN_PANELS = 28
EXPECTED_TRAIN_TRUTHS = 709
EXPECTED_DEV_SOURCES = 3
EXPECTED_DEV_REPORTS = 1
EXPECTED_DEV_PANELS = 9
EXPECTED_DEV_TRUTHS = 183


class TextExtentHeadInputError(head.ProductionHeadInputError):
    """The text-extent evidence does not form one authenticated experiment."""


@dataclass(frozen=True)
class RuntimeReportEvidence:
    split: str
    manifest_path: Path
    manifest_sha256: str
    report_path: Path
    report_sha256: str


def prepare_text_extent_capture_request(
    preflight_path: Path,
    expected_preflight_sha256: str,
    runtime_reports: Sequence[RuntimeReportEvidence],
    candidate_path: Path,
    expected_candidate_sha256: str,
    *,
    repository_root: Path = REPOSITORY_ROOT,
    capture_binary_root: Path | None = None,
    capture_source_path: Path | None = None,
) -> head.CapturePreparation:
    """Build the annotation-free request and saved-truth panel projections."""

    root = repository_root.resolve()
    preflight_path, preflight = _read_json(
        preflight_path, expected_preflight_sha256, root, "text-extent preflight"
    )
    _require_preflight(preflight)
    _authenticate_preflight_provenance(preflight, root)
    sources = _load_train_sources(preflight, preflight_path, root)

    truth_descriptor = _object(preflight.get("train_text_truth"), "train text truth")
    truth_path, truth = _read_json(
        root / _string(truth_descriptor, "path"),
        _sha(truth_descriptor.get("sha256"), "train text truth"),
        root,
        "train text truth",
    )
    if truth.get("schema") != TRAIN_TRUTH_SCHEMA:
        raise TextExtentHeadInputError("train text truth schema changed")

    historical = _object(preflight.get("historical_dev"), "historical dev evidence")
    historical_documents: dict[str, Mapping[str, Any]] = {}
    for key in ("binding", "manifest", "synthetic_truth"):
        descriptor = _object(historical.get(key), f"historical dev {key}")
        _, historical_documents[key] = _read_json(
            root / _string(descriptor, "path"),
            _sha(descriptor.get("sha256"), f"historical dev {key}"),
            root,
            f"historical dev {key}",
        )
    historical_dev_binding = _object(
        historical_documents["binding"].get("dev"), "historical dev runtime binding"
    )
    historical_manifest = _object(historical.get("manifest"), "historical dev manifest")
    if (
        _string(historical_dev_binding, "manifest_path")
        != _string(historical_manifest, "path")
        or _sha(historical_dev_binding.get("manifest_sha256"), "historical dev manifest")
        != _sha(historical_manifest.get("sha256"), "historical dev manifest")
    ):
        raise TextExtentHeadInputError("historical dev manifest differs from its fixed binding")
    oracle_descriptor = _object(historical.get("oracle_request"), "historical dev oracle")
    oracle_path, oracle = _read_json(
        root / _string(oracle_descriptor, "path"),
        _sha(oracle_descriptor.get("sha256"), "historical dev oracle"),
        root,
        "historical dev oracle",
    )
    if oracle.get("schema") != ORACLE_REQUEST_SCHEMA:
        raise TextExtentHeadInputError("historical dev oracle schema changed")

    candidate_path, candidate = _read_json(
        candidate_path, expected_candidate_sha256, root, "runtime candidate"
    )
    detector, detector_manifest_path, detector_manifest_sha = _authenticate_candidate(
        candidate, root
    )

    reports, capture_panels, panel_domains = _load_runtime_reports(
        runtime_reports,
        sources,
        historical_dev_binding,
        candidate,
        expected_candidate_sha256,
        root,
    )
    train_domains = tuple(item for item in panel_domains if item.runtime_input.split == "train")
    dev_domains = tuple(item for item in panel_domains if item.runtime_input.split == "validation")
    expected_dev_sources = {
        _sha(item, "historical dev source")
        for item in _array(historical.get("source_ids"), "historical dev source identities")
    }
    actual_dev_sources = {item.runtime_input.source_sha256 for item in dev_domains}
    if expected_dev_sources != actual_dev_sources:
        raise TextExtentHeadInputError("historical dev source identities changed")
    train = _build_train_split(truth, sources, train_domains)
    dev = _build_dev_split(oracle, dev_domains)
    if {item.source_sha256 for item in train.source_truths} & {
        item.source_sha256 for item in dev.source_truths
    }:
        raise TextExtentHeadInputError("train and fixed dev source identities overlap")
    tiled = production_tiled_inputs.ProductionTiledInputs(
        expected_preflight_sha256.lower(), train, dev
    )

    source_path = head._inside(
        root, capture_source_path or root / head.CAPTURE_SOURCE_PATH
    )
    binary_root = head._inside(
        root,
        capture_binary_root
        or root / "tools/GraphReader.SyntheticRuntimeEvidence/bin/Release/net10.0-windows/win-x64",
    )
    assemblies = []
    for name in head.CAPTURE_ASSEMBLY_NAMES:
        path = head._inside(root, binary_root / f"{name}.dll")
        assemblies.append(
            {"name": name, "path": head._repository_path(path, root), "sha256": _hash(path)}
        )

    request: dict[str, Any] = {
        "schema": head.CAPTURE_REQUEST_SCHEMA,
        "scope": head.CAPTURE_SCOPE,
        "synthetic_only": True,
        "private_data": False,
        "sealed_data": False,
        "truth_included": False,
        "model_inference": False,
        "training_input_ready": False,
        "production_approved": False,
        "capture_source": {
            "path": head._repository_path(source_path, root),
            "sha256": _hash(source_path),
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
        "reports": reports,
        "panels": [
            {
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
            for item in capture_panels
        ],
    }
    # The request sent to the application must remain annotation-free.
    encoded = json.dumps(request, sort_keys=True).lower()
    if any(token in encoded for token in ('"text"', 'truth_id', 'annotation')):
        raise TextExtentHeadInputError("capture request leaked text supervision")
    return head.CapturePreparation(
        expected_preflight_sha256.lower(), tiled, tuple(capture_panels), request
    )


def load_text_extent_head_inputs(
    preparation: head.CapturePreparation,
    capture_report_path: Path,
    expected_capture_report_sha256: str,
    *,
    repository_root: Path = REPOSITORY_ROOT,
) -> head.ProductionHeadInputs:
    """Authenticate a C# tensor capture, then join the prepared saved truth."""

    root = repository_root.resolve()
    report_path, report = _read_json(
        capture_report_path,
        expected_capture_report_sha256,
        root,
        "official-head tensor capture report",
    )
    request_descriptor = _object(report.get("request"), "capture request identity")
    request_path, captured_request = _read_json(
        root / _string(request_descriptor, "path"),
        _sha(request_descriptor.get("sha256"), "capture request"),
        root,
        "capture request",
    )
    if captured_request != preparation.request:
        raise TextExtentHeadInputError("captured request differs from prepared extent request")
    head._validate_report_header(report, preparation, root)
    records = _array(report.get("panels"), "tensor capture panels")
    if len(records) != EXPECTED_TRAIN_PANELS + EXPECTED_DEV_PANELS:
        raise TextExtentHeadInputError("tensor capture panel denominator changed")
    by_id: dict[str, Mapping[str, Any]] = {}
    for raw in records:
        record = _object(raw, "tensor capture panel")
        panel_id = _string(record, "panel_id")
        if panel_id in by_id:
            raise TextExtentHeadInputError("tensor capture repeats a panel identity")
        by_id[panel_id] = record
    requested = {item.panel_id: item for item in preparation.panels}
    if set(by_id) != set(requested):
        raise TextExtentHeadInputError("tensor capture panel inventory changed")
    train = head._load_split(
        preparation.tiled_inputs.train, by_id, requested, report_path, root
    )
    dev = head._load_split(
        preparation.tiled_inputs.dev, by_id, requested, report_path, root
    )
    return head.ProductionHeadInputs(
        preparation.binding_sha256,
        expected_capture_report_sha256.lower(),
        train,
        dev,
    )


def _require_preflight(value: Mapping[str, Any]) -> None:
    if (
        value.get("schema") != PREFLIGHT_SCHEMA
        or value.get("candidate_opened") is not False
        or type(value.get("optimizer_steps")) is not int
        or value.get("optimizer_steps") != 0
        or value.get("model_inference") is not False
        or value.get("private_data") is not False
        or value.get("sealed_data") is not False
    ):
        raise TextExtentHeadInputError("text-extent preflight scope or counters are invalid")


def _authenticate_preflight_provenance(value: Mapping[str, Any], root: Path) -> None:
    provenance = _object(value.get("source_provenance"), "preflight source provenance")
    diagnosis = _object(provenance.get("diagnosis"), "representation diagnosis")
    _, document = _read_json(
        root / _string(diagnosis, "path"),
        _sha(diagnosis.get("sha256"), "representation diagnosis"),
        root,
        "representation diagnosis",
    )
    if document.get("schema") != "graphreader.goal22.v43-representation-diagnosis.v1":
        raise TextExtentHeadInputError("representation diagnosis schema changed")
    sources = _array(provenance.get("current_generator_sources"), "generator sources")
    if not sources:
        raise TextExtentHeadInputError("preflight has no generator source provenance")
    seen: set[Path] = set()
    for raw in sources:
        item = _object(raw, "generator source")
        path = head._inside(root, root / _string(item, "path"))
        if path in seen or _hash(path) != _sha(item.get("sha256"), "generator source"):
            raise TextExtentHeadInputError("generator source provenance changed or repeats")
        seen.add(path)


def _load_train_sources(
    preflight: Mapping[str, Any], preflight_path: Path, root: Path
) -> Mapping[str, Mapping[str, Any]]:
    records = _array(preflight.get("sources"), "preflight train sources")
    if len(records) != EXPECTED_TRAIN_SOURCES:
        raise TextExtentHeadInputError("train source denominator changed")
    output: dict[str, Mapping[str, Any]] = {}
    for raw in records:
        item = _object(raw, "preflight train source")
        if item.get("split") != "train":
            raise TextExtentHeadInputError("preflight source carries a foreign split")
        source_sha = _sha(
            item.get("source_sha256", item.get("source_id")), "train source"
        )
        if item.get("source_id") not in (None, source_sha):
            raise TextExtentHeadInputError("train source identity disagrees with its PNG hash")
        if source_sha in output:
            raise TextExtentHeadInputError("preflight repeats a train source")
        for field in ("image", "scene", "annotation", "manifest"):
            path = head._inside(root, root / _string(item, field))
            expected = _sha(item.get(field + "_sha256"), field)
            if _hash(path) != expected:
                raise TextExtentHeadInputError(f"{field} checksum changed")
        if _hash(head._inside(root, root / _string(item, "image"))) != source_sha:
            raise TextExtentHeadInputError("train image bytes differ from source identity")
        width = item.get("width")
        height = item.get("height")
        if type(width) is not int or type(height) is not int or width <= 0 or height <= 0:
            raise TextExtentHeadInputError("train source dimensions are invalid")
        if type(item.get("seed")) is not int or not _string(item, "family"):
            raise TextExtentHeadInputError("train source seed or family is invalid")
        output[source_sha] = item
    return output


def _authenticate_candidate(
    candidate: Mapping[str, Any], root: Path
) -> tuple[Mapping[str, Any], Path, str]:
    if (
        candidate.get("schema") != "graphreader.local-synthetic-ocr-candidate.v1"
        or candidate.get("production_approved") is not False
    ):
        raise TextExtentHeadInputError("runtime candidate scope changed")
    detector = _object(candidate.get("detector"), "candidate detector")
    required_detector = {
        "model_path", "model_id", "model_version", "model_sha256",
        "manifest_path", "manifest_sha256",
    }
    if not required_detector.issubset(detector):
        raise TextExtentHeadInputError("candidate detector descriptor is incomplete")
    if (
        _string(detector, "model_id") != "PP-OCRv5_mobile_det"
        or _string(detector, "model_version") != "5.0.0"
        or _sha(detector.get("model_sha256"), "detector model")
        != "d4aa24d408cd70b8b9f66cc758e20f397fc31a9c69d8477cf8887fc53bd5fceb"
    ):
        raise TextExtentHeadInputError("candidate detector is not the pinned official head")
    model_path = Path(_string(detector, "model_path")).resolve()
    if _hash(model_path) != _sha(detector.get("model_sha256"), "detector model"):
        raise TextExtentHeadInputError("detector model checksum changed")
    manifest_path = Path(_string(detector, "manifest_path")).resolve()
    manifest_sha = _sha(detector.get("manifest_sha256"), "detector manifest")
    if _hash(manifest_path) != manifest_sha:
        raise TextExtentHeadInputError("detector manifest checksum changed")
    _, manifest = _read_json(manifest_path, manifest_sha, manifest_path.parent, "detector manifest")
    license_record = _object(manifest.get("license"), "detector license")
    providers = _array(manifest.get("providers"), "detector providers")
    if (
        manifest.get("model_id") != detector.get("model_id")
        or manifest.get("model_version") != detector.get("model_version")
        or manifest.get("task") != "ocr_detection"
        or manifest.get("sha256") != detector.get("model_sha256")
        or license_record.get("spdx") != "Apache-2.0"
        or license_record.get("reviewed") is not True
        or manifest.get("commercial_use") is not True
        or manifest.get("redistribution") is not True
        or "cpu" not in {str(item).lower() for item in providers}
    ):
        raise TextExtentHeadInputError("detector manifest identity or license changed")
    native_path = Path(_string(candidate, "native_path")).resolve()
    if _hash(native_path) != _sha(candidate.get("native_sha256"), "native runtime"):
        raise TextExtentHeadInputError("native runtime checksum changed")
    if candidate.get("native_scope") != "reviewed-source-runtime-local-diagnostic":
        raise TextExtentHeadInputError("native runtime scope changed")
    licenses = _array(candidate.get("license_inputs"), "license inputs")
    if len(licenses) != 2:
        raise TextExtentHeadInputError("detector license evidence is incomplete")
    seen_licenses: set[tuple[Path, str]] = set()
    for raw in licenses:
        item = _object(raw, "license input")
        if set(item) != {"path", "sha256"}:
            raise TextExtentHeadInputError("license input fields changed")
        path = Path(_string(item, "path")).resolve()
        digest = _sha(item.get("sha256"), "license input")
        identity = (path, digest)
        if identity in seen_licenses or _hash(path) != digest:
            raise TextExtentHeadInputError("license input checksum changed")
        seen_licenses.add(identity)
    return detector, manifest_path, manifest_sha


def _load_runtime_reports(
    descriptors: Sequence[RuntimeReportEvidence],
    train_sources: Mapping[str, Mapping[str, Any]],
    historical_dev_binding: Mapping[str, Any] | None,
    candidate: Mapping[str, Any],
    candidate_sha: str,
    root: Path,
) -> tuple[list[dict[str, Any]], list[head.CapturePanelInput], list[Any]]:
    if (
        len(descriptors) != EXPECTED_TRAIN_REPORTS + EXPECTED_DEV_REPORTS
        or sum(item.split == "train" for item in descriptors) != EXPECTED_TRAIN_REPORTS
        or sum(item.split == "validation" for item in descriptors) != EXPECTED_DEV_REPORTS
    ):
        raise TextExtentHeadInputError("runtime report inventory must be five train and one validation")
    reports: list[dict[str, Any]] = []
    capture_panels: list[head.CapturePanelInput] = []
    domains: list[Any] = []
    report_paths: set[Path] = set()
    panel_ids: set[str] = set()
    panel_hashes: set[str] = set()
    observed_sources: dict[str, set[str]] = {"train": set(), "validation": set()}
    expected_manifests = {
        "train": {
            (
                head._inside(root, root / _string(item, "manifest")),
                _sha(item.get("manifest_sha256"), "train manifest"),
            )
            for item in train_sources.values()
        },
        "validation": set(),
    }
    expected_dev_report: tuple[Path, str] | None = None
    if historical_dev_binding is not None:
        expected_manifests["validation"].add(
            (
                head._inside(root, root / _string(historical_dev_binding, "manifest_path")),
                _sha(historical_dev_binding.get("manifest_sha256"), "historical dev manifest"),
            )
        )
        expected_dev_report = (
            head._inside(root, root / _string(historical_dev_binding, "report_path")),
            _sha(historical_dev_binding.get("report_sha256"), "historical dev report"),
        )
    observed_manifests: dict[str, set[tuple[Path, str]]] = {
        "train": set(), "validation": set()
    }
    for descriptor in descriptors:
        if descriptor.split not in observed_sources:
            raise TextExtentHeadInputError("runtime report has an invalid split")
        manifest_path, manifest = _read_json(
            descriptor.manifest_path,
            descriptor.manifest_sha256,
            root,
            "runtime source manifest",
        )
        manifest_identity = (manifest_path, descriptor.manifest_sha256.lower())
        if manifest_identity not in expected_manifests[descriptor.split]:
            raise TextExtentHeadInputError("runtime manifest is absent from the fixed split binding")
        if manifest_identity in observed_manifests[descriptor.split]:
            raise TextExtentHeadInputError("runtime manifest repeats")
        observed_manifests[descriptor.split].add(manifest_identity)
        if manifest.get("schema") != "graphreader.synthetic-runtime-raster-inputs.v1":
            raise TextExtentHeadInputError("runtime source manifest schema changed")
        if manifest.get("split") != descriptor.split or manifest.get("contains_truth") is not False:
            raise TextExtentHeadInputError("runtime source manifest split or truth scope changed")
        manifest_sources: dict[str, tuple[int, int]] = {}
        for raw_image in _array(manifest.get("images"), "runtime manifest images"):
            image = _object(raw_image, "runtime manifest image")
            source_sha = _sha(image.get("image_sha256"), "manifest source")
            width, height = image.get("width"), image.get("height")
            if (
                image.get("split") != descriptor.split
                or type(width) is not int or width <= 0
                or type(height) is not int or height <= 0
                or source_sha in manifest_sources
            ):
                raise TextExtentHeadInputError("runtime manifest source inventory is invalid")
            source_path = head._inside(root, manifest_path.parent / _string(image, "image"))
            if _hash(source_path) != source_sha:
                raise TextExtentHeadInputError("runtime manifest source bytes changed")
            manifest_sources[source_sha] = (width, height)
        report_path, report = _read_json(
            descriptor.report_path, descriptor.report_sha256, root, "runtime report"
        )
        if report_path in report_paths:
            raise TextExtentHeadInputError("runtime report path repeats")
        report_paths.add(report_path)
        if descriptor.split == "validation" and (report_path, descriptor.report_sha256.lower()) != expected_dev_report:
            raise TextExtentHeadInputError("historical dev report differs from its fixed binding")
        if (
            report.get("schema") != "graphreader.synthetic-runtime-seed-evidence.v2"
            or report.get("scope") != "local-synthetic-seed-diagnostic"
            or report.get("production_approved") is not False
            or report.get("training_input_ready") is not False
            or report.get("input_manifest_sha256") != descriptor.manifest_sha256.lower()
            or report.get("candidate_sha256") != candidate_sha.lower()
            or report.get("native_scope") != candidate.get("native_scope")
            or report.get("failed_panels") != 0
            or report.get("completed_panels") != report.get("panel_count")
            or report.get("failed") != 0
            or report.get("completed") != report.get("count")
        ):
            raise TextExtentHeadInputError("runtime report is incomplete or belongs to another input")
        if report.get("native_sha256") != candidate.get("native_sha256"):
            raise TextExtentHeadInputError("runtime report native identity changed")
        reports.append(
            {
                "split": descriptor.split,
                "manifest_path": head._repository_path(manifest_path, root),
                "manifest_sha256": descriptor.manifest_sha256.lower(),
                "report_path": head._repository_path(report_path, root),
                "report_sha256": descriptor.report_sha256.lower(),
            }
        )
        dataset_seed = manifest.get("seed")
        if type(dataset_seed) is not int:
            raise TextExtentHeadInputError("runtime manifest seed is invalid")
        cases = _array(report.get("cases"), "runtime cases")
        if len(cases) != len(manifest_sources):
            raise TextExtentHeadInputError("runtime report source count differs from its manifest")
        report_panel_count = 0
        report_sources: set[str] = set()
        for raw_case in cases:
            case = _object(raw_case, "runtime case")
            source_sha = _sha(case.get("image_sha256"), "runtime source")
            if source_sha in report_sources:
                raise TextExtentHeadInputError("runtime report repeats a source")
            report_sources.add(source_sha)
            observed_sources[descriptor.split].add(source_sha)
            source_info = train_sources.get(source_sha)
            if descriptor.split == "train" and source_info is None:
                raise TextExtentHeadInputError("runtime train report contains an unbound source")
            family = (
                _string(source_info, "family") if source_info is not None else "fixed-historical-dev"
            )
            scene_seed = source_info.get("seed", 0) if source_info is not None else 0
            source_width = case.get("width")
            source_height = case.get("height")
            if (
                type(source_width) is not int or type(source_height) is not int
                or manifest_sources.get(source_sha) != (source_width, source_height)
                or case.get("status") != "panels-completed"
            ):
                raise TextExtentHeadInputError("runtime source identity differs from its manifest")
            if source_info is not None and (
                source_info.get("width") != source_width or source_info.get("height") != source_height
            ):
                raise TextExtentHeadInputError("runtime source dimensions differ from preflight")
            for raw_panel in _array(case.get("panels"), "runtime panels"):
                report_panel_count += 1
                panel = _object(raw_panel, "runtime panel")
                panel_id = _string(panel, "panel_id")
                panel_sha = _sha(panel.get("image_sha256"), "runtime panel")
                if panel.get("status") != "seed-completed" or panel_id in panel_ids:
                    raise TextExtentHeadInputError("runtime panel is failed or duplicated")
                if panel_sha in panel_hashes:
                    raise TextExtentHeadInputError("runtime panel PNG payload repeats")
                panel_ids.add(panel_id)
                panel_hashes.add(panel_sha)
                width = panel.get("width")
                height = panel.get("height")
                crop_object = _object(panel.get("crop"), "runtime panel crop")
                crop = tuple(crop_object.get(key) for key in ("x", "y", "width", "height"))
                if (
                    type(width) is not int
                    or type(height) is not int
                    or any(type(value) is not int for value in crop)
                    or crop[2:] != (width, height)
                    or crop[0] < 0 or crop[1] < 0
                    or crop[0] + crop[2] > source_width
                    or crop[1] + crop[3] > source_height
                ):
                    raise TextExtentHeadInputError("runtime panel dimensions or crop changed")
                requested = _box_object(panel.get("requested_crop"), "requested crop")
                source_to_panel = _matrix(panel.get("source_to_panel_matrix"), "source-to-panel")
                panel_to_source = _matrix(panel.get("panel_to_source_matrix"), "panel-to-source")
                expected_forward = (
                    1.0, 0.0, -float(crop[0]), 0.0, 1.0, -float(crop[1]), 0.0, 0.0, 1.0
                )
                expected_inverse = (
                    1.0, 0.0, float(crop[0]), 0.0, 1.0, float(crop[1]), 0.0, 0.0, 1.0
                )
                if (
                    requested[0] < crop[0] or requested[1] < crop[1]
                    or requested[0] + requested[2] > crop[0] + crop[2]
                    or requested[1] + requested[3] > crop[1] + crop[3]
                    or source_to_panel != expected_forward
                    or panel_to_source != expected_inverse
                    or panel.get("source_image_sha256") != source_sha
                    or panel.get("source_width") != source_width
                    or panel.get("source_height") != source_height
                ):
                    raise TextExtentHeadInputError("runtime panel provenance or transforms changed")
                png = _object(panel.get("panel_png"), "runtime panel PNG")
                if _string(png, "file") != "panel.png":
                    raise TextExtentHeadInputError("runtime panel PNG name changed")
                png_path = head._inside(
                    root, report_path.parent / source_sha / panel_id / _string(png, "file")
                )
                png_sha = _sha(png.get("sha256"), "panel PNG")
                byte_count = png.get("byte_count")
                if type(byte_count) is not int:
                    raise TextExtentHeadInputError("panel PNG byte count is invalid")
                encoded = _read_exact(png_path, png_sha, byte_count, "panel PNG")
                if png_sha != panel_sha:
                    raise TextExtentHeadInputError("panel PNG identity differs from runtime panel")
                gray, bgr = head._decode_production_pixels(encoded, width, height)
                diagnostic = _object(panel.get("ocr_proposal_diagnostic"), "OCR diagnostic")
                gray_sha = _sha(diagnostic.get("unmasked_input_sha256"), "recorded Gray8")
                if sha256(gray.tobytes(order="C")).hexdigest() != gray_sha:
                    raise TextExtentHeadInputError("decoded Gray8 differs from runtime OCR input")
                bgr_sha = sha256(bgr.tobytes(order="C")).hexdigest()
                runtime = SimpleNamespace(
                    split=descriptor.split,
                    dataset_seed=dataset_seed,
                    family=family,
                    scene_seed=scene_seed,
                    source_sha256=source_sha,
                    panel_id=panel_id,
                    panel_sha256=panel_sha,
                    width=width,
                    height=height,
                    crop=crop,
                    requested_crop=requested,
                    gray8=gray,
                )
                domain = SimpleNamespace(
                    runtime_input=runtime,
                    source_width=source_width,
                    source_height=source_height,
                    source_to_panel_matrix=source_to_panel,
                    panel_to_source_matrix=panel_to_source,
                )
                domains.append(domain)
                capture_panels.append(
                    head.CapturePanelInput(
                        descriptor.split, source_sha, panel_id, panel_sha, width, height, crop,
                        report_path, descriptor.report_sha256.lower(), png_path, png_sha,
                        byte_count, gray_sha, bgr_sha,
                    )
                )
        if report_sources != set(manifest_sources):
            raise TextExtentHeadInputError("runtime report source inventory differs from its manifest")
        if report_panel_count != report.get("panel_count"):
            raise TextExtentHeadInputError("runtime report panel count differs from its cases")
    if observed_manifests != expected_manifests:
        raise TextExtentHeadInputError("runtime manifest inventory changed")
    if observed_sources["train"] != set(train_sources):
        raise TextExtentHeadInputError("runtime train source inventory changed")
    if len(observed_sources["validation"]) != EXPECTED_DEV_SOURCES:
        raise TextExtentHeadInputError("fixed dev source denominator changed")
    if observed_sources["train"] & observed_sources["validation"]:
        raise TextExtentHeadInputError("train and fixed dev runtime sources overlap")
    if (
        sum(item.runtime_input.split == "train" for item in domains) != EXPECTED_TRAIN_PANELS
        or sum(item.runtime_input.split == "validation" for item in domains) != EXPECTED_DEV_PANELS
    ):
        raise TextExtentHeadInputError("runtime panel denominator changed")
    return reports, capture_panels, domains


def _build_train_split(
    truth_document: Mapping[str, Any],
    sources: Mapping[str, Mapping[str, Any]],
    domains: Sequence[Any],
) -> production_tiled_inputs.ProductionTiledSplit:
    if (
        truth_document.get("schema") != TRAIN_TRUTH_SCHEMA
        or truth_document.get("split") != "train"
        or truth_document.get("source_count") != EXPECTED_TRAIN_SOURCES
        or truth_document.get("truth_count") != EXPECTED_TRAIN_TRUTHS
        or truth_document.get("coordinate_space") != "original_pixels"
        or truth_document.get("synthetic_only") is not True
        or truth_document.get("private_data") is not False
        or truth_document.get("sealed_data") is not False
    ):
        raise TextExtentHeadInputError("train truth document scope or denominators changed")
    records = _array(truth_document.get("truths"), "train text truths")
    if len(records) != EXPECTED_TRAIN_TRUTHS:
        raise TextExtentHeadInputError("train full truth denominator changed")
    by_source: dict[str, list[Any]] = {}
    for domain in domains:
        by_source.setdefault(domain.runtime_input.source_sha256, []).append(domain)
    projections: dict[str, list[production_tiled_inputs.PanelTextProjection]] = {
        item.runtime_input.panel_id: [] for item in domains
    }
    truths: list[production_tiled_inputs.SourceTextTruth] = []
    seen_ids: set[str] = set()
    seen_text_ids: set[tuple[str, str]] = set()
    truth_counts: dict[str, int] = {source_sha: 0 for source_sha in sources}
    for raw in records:
        item = _object(raw, "train text truth")
        source_sha = _sha(item.get("source_sha256", item.get("source_id")), "truth source")
        if source_sha not in sources:
            raise TextExtentHeadInputError("train truth references an unbound source")
        if item.get("source_id") != source_sha:
            raise TextExtentHeadInputError("train truth source identity disagrees with its hash")
        truth_id = _sha(item.get("truth_id"), "truth identity")
        text_id = _string(item, "text_id")
        if truth_id in seen_ids or (source_sha, text_id) in seen_text_ids:
            raise TextExtentHeadInputError("train truth identity repeats")
        seen_ids.add(truth_id)
        seen_text_ids.add((source_sha, text_id))
        if item.get("coordinate_space") != "original_pixels" or not _string(item, "text"):
            raise TextExtentHeadInputError("train truth text must be nonempty in original pixels")
        if not _string(item, "region_id") or not _string(item, "role"):
            raise TextExtentHeadInputError("train truth region or role is invalid")
        truth_counts[source_sha] += 1
        box = _box(item.get("source_box_ltrb"), "train source box")
        source = sources[source_sha]
        production_tiled_inputs._validate_box(
            box, source["width"], source["height"], "train source text"
        )
        projected = tuple(
            projection
            for domain in by_source.get(source_sha, ())
            if (projection := production_tiled_inputs._project_truth(
                truth_id, text_id, box, domain
            )) is not None
        )
        for projection in projected:
            projections[projection.panel_id].append(projection)
        first = by_source[source_sha][0].runtime_input
        truths.append(
            production_tiled_inputs.SourceTextTruth(
                truth_id, "train", first.dataset_seed, first.family, first.scene_seed,
                source_sha, text_id, _string(item, "role"), box, projected,
                production_tiled_inputs._projection_status(box, projected),
            )
        )
    if any(
        truth_counts[source_sha] != source.get("text_truth_count")
        for source_sha, source in sources.items()
    ):
        raise TextExtentHeadInputError("train per-source truth denominator changed")
    panels = tuple(
        _tiled_panel(domain, tuple(projections[domain.runtime_input.panel_id]))
        for domain in sorted(domains, key=lambda value: value.runtime_input.panel_id)
    )
    return _split("train", truths, panels, EXPECTED_TRAIN_SOURCES)


def _build_dev_split(
    oracle: Mapping[str, Any], domains: Sequence[Any]
) -> production_tiled_inputs.ProductionTiledSplit:
    actual = {item.runtime_input.panel_id: item for item in domains}
    oracle_panels = [
        _object(item, "historical oracle panel")
        for item in _array(oracle.get("panels"), "historical oracle panels")
        if _object(item, "historical oracle panel").get("split") == "validation"
    ]
    if len(oracle_panels) != EXPECTED_DEV_PANELS:
        raise TextExtentHeadInputError("historical dev oracle panel denominator changed")
    projections: dict[str, list[production_tiled_inputs.PanelTextProjection]] = {
        panel_id: [] for panel_id in actual
    }
    grouped: dict[str, list[tuple[Any, tuple[float, float, float, float]]]] = {}
    seen_panels: set[str] = set()
    for panel in oracle_panels:
        panel_id = _string(panel, "panel_id")
        domain = actual.get(panel_id)
        if panel_id in seen_panels:
            raise TextExtentHeadInputError("historical dev oracle repeats a panel")
        seen_panels.add(panel_id)
        if domain is None or panel.get("source_sha256") != domain.runtime_input.source_sha256:
            raise TextExtentHeadInputError("historical dev oracle differs from runtime panels")
        if panel.get("width") != domain.runtime_input.width or panel.get("height") != domain.runtime_input.height:
            raise TextExtentHeadInputError("historical dev oracle panel dimensions changed")
        if _matrix(panel.get("panel_to_source_matrix"), "historical dev panel-to-source") != domain.panel_to_source_matrix:
            raise TextExtentHeadInputError("historical dev runtime geometry changed")
        for raw in _array(panel.get("projections"), "historical dev projections"):
            item = _object(raw, "historical dev projection")
            truth_id = _sha(item.get("truth_id"), "historical dev truth")
            panel_box = _box(item.get("panel_box"), "historical dev panel box")
            production_tiled_inputs._validate_box(
                panel_box, domain.runtime_input.width, domain.runtime_input.height,
                "historical dev panel text",
            )
            corners = tuple(
                _map_point(domain.panel_to_source_matrix, x, y)
                for x, y in (
                    (panel_box[0], panel_box[1]), (panel_box[2], panel_box[1]),
                    (panel_box[2], panel_box[3]), (panel_box[0], panel_box[3]),
                )
            )
            source_box = (
                min(point[0] for point in corners), min(point[1] for point in corners),
                max(point[0] for point in corners), max(point[1] for point in corners),
            )
            projection = production_tiled_inputs.PanelTextProjection(
                truth_id, truth_id, panel_id, source_box, panel_box, "full"
            )
            projections[panel_id].append(projection)
            grouped.setdefault(truth_id, []).append((domain, source_box))
    if seen_panels != set(actual):
        raise TextExtentHeadInputError("historical dev panel inventory changed")
    if len(grouped) != EXPECTED_DEV_TRUTHS:
        raise TextExtentHeadInputError("historical dev full truth denominator changed")
    truths: list[production_tiled_inputs.SourceTextTruth] = []
    for truth_id, entries in sorted(grouped.items()):
        source_ids = {entry[0].runtime_input.source_sha256 for entry in entries}
        if len(source_ids) != 1:
            raise TextExtentHeadInputError("historical dev truth crosses source identities")
        source_box = entries[0][1]
        if any(any(abs(a - b) > 1e-6 for a, b in zip(source_box, box)) for _, box in entries[1:]):
            raise TextExtentHeadInputError("historical dev truth projections disagree")
        runtime = entries[0][0].runtime_input
        truth_projections = tuple(
            projection
            for panel_items in projections.values()
            for projection in panel_items
            if projection.truth_id == truth_id
        )
        truths.append(
            production_tiled_inputs.SourceTextTruth(
                truth_id, "validation", runtime.dataset_seed, runtime.family,
                runtime.scene_seed, runtime.source_sha256, truth_id, "other",
                source_box, truth_projections,
                production_tiled_inputs._projection_status(source_box, truth_projections),
            )
        )
    panels = tuple(
        _tiled_panel(domain, tuple(projections[domain.runtime_input.panel_id]))
        for domain in sorted(domains, key=lambda value: value.runtime_input.panel_id)
    )
    return _split("validation", truths, panels, EXPECTED_DEV_SOURCES)


def _tiled_panel(
    domain: Any, projections: tuple[production_tiled_inputs.PanelTextProjection, ...]
) -> production_tiled_inputs.ProductionTiledPanel:
    runtime = domain.runtime_input
    return production_tiled_inputs.ProductionTiledPanel(
        runtime.split, runtime.dataset_seed, runtime.family, runtime.scene_seed,
        runtime.source_sha256, runtime.panel_id, runtime.panel_sha256,
        runtime.width, runtime.height, runtime.crop, runtime.requested_crop,
        domain.source_to_panel_matrix, domain.panel_to_source_matrix,
        runtime.gray8, projections, (),
    )


def _split(
    name: str,
    truths: Sequence[production_tiled_inputs.SourceTextTruth],
    panels: Sequence[production_tiled_inputs.ProductionTiledPanel],
    expected_sources: int,
) -> production_tiled_inputs.ProductionTiledSplit:
    projected = sum(bool(item.projections) for item in truths)
    partial = sum(any(value.status == "partial" for value in item.projections) for item in truths)
    overlapping = sum(len(item.projections) > 1 for item in truths)
    source_count = len({item.source_sha256 for item in truths})
    if source_count != expected_sources:
        raise TextExtentHeadInputError(f"{name} truth source denominator changed")
    return production_tiled_inputs.ProductionTiledSplit(
        name, source_count, len(panels), len(truths), projected,
        len(truths) - projected, partial, overlapping, tuple(truths), tuple(panels),
    )


def _read_json(
    path: Path, expected_sha: str, root: Path, label: str
) -> tuple[Path, Mapping[str, Any]]:
    try:
        resolved = head._inside(root, path if path.is_absolute() else root / path)
    except head.ProductionHeadInputError as exception:
        raise TextExtentHeadInputError(str(exception)) from exception
    payload = _read_exact(resolved, _sha(expected_sha, label), None, label)
    def unique_fields(pairs):
        value = {}
        for key, item in pairs:
            if key in value:
                raise TextExtentHeadInputError(f"{label} repeats a JSON field")
            value[key] = item
        return value

    def invalid_constant(_value):
        raise TextExtentHeadInputError(f"{label} contains a nonfinite JSON value")

    try:
        document = json.loads(payload, object_pairs_hook=unique_fields, parse_constant=invalid_constant)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise TextExtentHeadInputError(f"{label} is not valid JSON") from error
    return resolved, _object(document, label)


def _read_exact(
    path: Path, expected_sha: str, expected_byte_count: int | None, label: str
) -> bytes:
    try:
        return head._read_exact(path, expected_sha, expected_byte_count, label)
    except head.ProductionHeadInputError as exception:
        raise TextExtentHeadInputError(str(exception)) from exception


def _hash(path: Path) -> str:
    return sha256(path.read_bytes()).hexdigest()


def _object(value: Any, label: str) -> Mapping[str, Any]:
    try:
        return head._object(value, label)
    except head.ProductionHeadInputError as exception:
        raise TextExtentHeadInputError(str(exception)) from exception


def _array(value: Any, label: str) -> Sequence[Any]:
    try:
        return head._array(value, label)
    except head.ProductionHeadInputError as exception:
        raise TextExtentHeadInputError(str(exception)) from exception


def _string(value: Mapping[str, Any], key: str) -> str:
    try:
        return head._string(value, key)
    except head.ProductionHeadInputError as exception:
        raise TextExtentHeadInputError(str(exception)) from exception


def _sha(value: Any, label: str) -> str:
    try:
        return head._sha(value, label)
    except head.ProductionHeadInputError as exception:
        raise TextExtentHeadInputError(str(exception)) from exception


def _box(value: Any, label: str) -> tuple[float, float, float, float]:
    if not isinstance(value, list) or len(value) != 4:
        raise TextExtentHeadInputError(f"{label} must be a four-value array")
    if any(type(item) not in (int, float) or not np.isfinite(item) for item in value):
        raise TextExtentHeadInputError(f"{label} contains a nonnumeric value")
    return tuple(float(item) for item in value)  # type: ignore[return-value]


def _box_object(value: Any, label: str) -> tuple[float, float, float, float]:
    item = _object(value, label)
    values = [item.get(key) for key in ("x", "y", "width", "height")]
    if any(type(number) not in (int, float) or not np.isfinite(number) for number in values):
        raise TextExtentHeadInputError(f"{label} is invalid")
    return tuple(float(number) for number in values)  # type: ignore[return-value]


def _matrix(value: Any, label: str) -> tuple[float, ...]:
    if not isinstance(value, list) or len(value) != 9 or any(
        type(item) not in (int, float) or not np.isfinite(item) for item in value
    ):
        raise TextExtentHeadInputError(f"{label} matrix is invalid")
    result = tuple(float(item) for item in value)
    if abs(result[6]) > 1e-9 or abs(result[7]) > 1e-9 or abs(result[8] - 1.0) > 1e-9:
        raise TextExtentHeadInputError(f"{label} matrix is not affine")
    return result


def _map_point(matrix: Sequence[float], x: float, y: float) -> tuple[float, float]:
    return (
        matrix[0] * x + matrix[1] * y + matrix[2],
        matrix[3] * x + matrix[4] * y + matrix[5],
    )


__all__ = [
    "RuntimeReportEvidence",
    "TextExtentHeadInputError",
    "load_text_extent_head_inputs",
    "prepare_text_extent_capture_request",
]
