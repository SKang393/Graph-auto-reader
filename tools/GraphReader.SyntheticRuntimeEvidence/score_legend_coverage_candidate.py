# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Score one legend-coverage candidate on authenticated 37+6 panel evidence."""
from __future__ import annotations

import argparse
from copy import deepcopy
from hashlib import sha256
import json
from pathlib import Path
import time
from types import SimpleNamespace
from typing import Any, Mapping, Sequence

import score_full_ocr_candidate_v2 as metric
import score_text_extent_candidate as historical_truth
from ml.ocr.official_bakeoff import (
    legend_coverage_head_inputs as legend_bridge,
    supplemental_head_features,
    text_extent_head_inputs as bridge,
)

ROOT = Path(__file__).resolve().parents[2]
OUTPUT_SCHEMA = "graphreader.legend-coverage-candidate-score.v1"
SUPPLEMENTAL_EVALUATION_SCHEMA = (
    "graphreader.supplemental-head-candidate-evaluation.v1"
)
METRIC_SHA256 = "8b879664c33e83f2aaec0e637f4f0f4df0f9701fe04ac43867672dde2e662656"
HISTORICAL_TRUTH_SHA256 = (
    "41d6438b5198c2cb7f89b1d6a26c720b4a3089d8a49666a7c7cefad5ce3fe050"
)
ORIGINAL_REQUEST_SHA256 = (
    "7d62e9e962d01448fd193887573b6a2ab882574eba41f67a543c444c6222c059"
)
ORIGINAL_CAPTURE_SHA256 = (
    "15e48b27ce3b8f5f53311abbef7235c35b3e8e361c8baddb09a4d3a7e7d5938e"
)
SUPPLEMENTAL_REQUEST_SHA256 = (
    "0e795a4d17b1b1d0b2f652a30ffff30e038ec51eae61a9699510d65b43eb6cf1"
)
SUPPLEMENTAL_CAPTURE_SHA256 = (
    "2c605fb18cb0195453f1b569506546c22c3a873db19475e1fb87daa0bddaeb19"
)
SUPPLEMENTAL_TRUTH_PATH = Path(
    "artifacts/goal22-runs/ocr-train-legend-head-inputs-v1/"
    "supplemental-train-text-truth.json"
)
SUPPLEMENTAL_TRUTH_SHA256 = (
    "13174cba2e740b47d1fc68f0e15c2078c720526be6475b91a8740e51195150b3"
)
HISTORICAL_BINARY_ROOT = Path(
    "artifacts/goal22-runs/ocr-v44-text-extent/runtime-before-word-stress-20260917"
)
HISTORICAL_BINARY_MANIFEST = HISTORICAL_BINARY_ROOT / "backup-manifest.json"
HISTORICAL_BINARY_MANIFEST_SHA256 = (
    "ed06006c6a4fcc078fc13817571a4fb58cd244add12731971ad8e7845be06aae"
)
HISTORICAL_SOURCE = Path(
    "artifacts/goal22-runs/ocr-supplemental-capture-base-snapshot/"
    "OfficialHeadTensorCapture.cs"
)
HISTORICAL_SOURCE_SHA256 = (
    "ada4110d386581e87de8f8693c86e045e41566270d601a836c79d41c86faf3a4"
)
HISTORICAL_SOURCE_BINDING = Path(
    "artifacts/goal22-runs/ocr-supplemental-capture-base-snapshot/binding.json"
)
HISTORICAL_SOURCE_BINDING_SHA256 = (
    "b6572afe3bfcfffb4a27b0fca3e42bdcae8f83f77b9027a28cdb2f73e197bf87"
)
CURRENT_EVALUATOR_SOURCE = (
    "tools/GraphReader.SyntheticRuntimeEvidence/OfficialHeadCandidateEvaluation.cs"
)
CURRENT_EVALUATOR_SOURCE_SHA256 = (
    "1575e1f47669a0cb7f644d3318100384ac4cb2bf583cd60685fdcb8bd4c1560d"
)
CANDIDATE_TEMPLATE_PATH = Path(
    "artifacts/goal22-runs/ocr-v45-evaluation-inputs-v1/candidate-template.json"
)
CANDIDATE_TEMPLATE_SHA256 = (
    "fb69efa132597ff23f5b4096894fae3991e31eeb8b1559ed9e50ee68fe9e600b"
)
DERIVED_MODEL_VERSION = "0.0.1"
PARENT_MODEL_VERSION = "5.0.0"
MODIFICATION_NOTICE_NAME = "MODIFICATION-NOTICE.txt"
MODIFICATION_NOTICE_TEXT = (
    "Derived from the Apache-2.0 PaddleOCR PP-OCRv5 mobile detector.\n"
    "Copyright 2026 Sungwoo Kang. Nine DB head constants trained on project-owned synthetic train data.\n"
    "Frozen trunk and batch-normalization statistics remain unchanged. Not production approved.\n"
    "Original notices and license remain in LICENSES/PaddlePaddle-PP-OCRv5-Models-Notice.txt and\n"
    "LICENSES/PaddlePaddle-PP-OCRv5-Models-Apache-2.0.txt.\n"
)


def _descriptor(root: Path, path: Path, digest: str) -> dict[str, str]:
    resolved = path.resolve()
    if not resolved.is_relative_to(root):
        raise metric.EvidenceError("Evidence path escaped the repository")
    return {"path": resolved.relative_to(root).as_posix(), "sha256": digest}


def _read_json(root: Path, path: Path, digest: str, label: str) -> Mapping[str, Any]:
    _, payload = metric.geometry._read_exact(root, str(path), digest, label)
    return metric.geometry._json(payload, label)


def _read_json_array(root: Path, path: Path, digest: str, label: str) -> Sequence[Any]:
    _, payload = metric.geometry._read_exact(root, str(path), digest, label)
    try:
        value = json.loads(payload)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise metric.EvidenceError(f"{label} is invalid JSON") from error
    if not isinstance(value, list):
        raise metric.EvidenceError(f"{label} must be an array")
    return value


def _validate_historical_assemblies(root: Path, request: Mapping[str, Any]) -> None:
    rows = _read_json_array(
        root, root / HISTORICAL_BINARY_MANIFEST,
        HISTORICAL_BINARY_MANIFEST_SHA256, "historical binary manifest",
    )
    by_original: dict[str, Mapping[str, Any]] = {}
    for raw in rows:
        row = metric.geometry._object(raw, "historical binary record")
        if set(row) != {"sha256", "backup_path", "original_path"}:
            raise metric.EvidenceError("Historical binary manifest shape changed")
        original = str(row.get("original_path", "")).replace("\\", "/")
        if not original or original in by_original:
            raise metric.EvidenceError("Historical binary manifest repeats a path")
        by_original[original] = row
    assemblies = metric.geometry._array(
        request.get("assemblies"), "historical capture assemblies"
    )
    if len(assemblies) != 4:
        raise metric.EvidenceError("Historical capture assembly count changed")
    names: set[str] = set()
    for raw in assemblies:
        assembly = metric.geometry._object(raw, "historical capture assembly")
        if set(assembly) != {"name", "path", "sha256"}:
            raise metric.EvidenceError("Historical capture assembly shape changed")
        name = assembly.get("name")
        original = str(assembly.get("path", "")).replace("\\", "/")
        row = by_original.get(original)
        if (name not in metric.geometry.EXPECTED_ASSEMBLIES or name in names
                or row is None or row.get("sha256") != assembly.get("sha256")):
            raise metric.EvidenceError("Historical capture assembly binding changed")
        metric.geometry._read_exact(
            root, row.get("backup_path"), row.get("sha256"),
            f"historical capture assembly {name}",
        )
        names.add(name)
    if names != metric.geometry.EXPECTED_ASSEMBLIES:
        raise metric.EvidenceError("Historical capture assembly inventory changed")


def _validate_scoring_sources(root: Path) -> dict[str, str]:
    expected = dict(metric.geometry.SOURCE_BINDINGS)
    for relative, digest in metric.SOURCE_BINDINGS.items():
        if relative in expected and expected[relative] != digest:
            raise metric.EvidenceError(
                f"Frozen scorer source bindings disagree: {relative}"
            )
        expected[relative] = digest
    observed: dict[str, str] = {}
    for relative, frozen_digest in expected.items():
        digest = (
            CURRENT_EVALUATOR_SOURCE_SHA256
            if relative == CURRENT_EVALUATOR_SOURCE else frozen_digest
        )
        metric.geometry._read_exact(root, relative, digest, f"scoring dependency {relative}")
        observed[relative] = digest
    return observed


def _require_candidate_template_binding(
    root: Path, template_path: Path, template_sha256: str,
) -> None:
    if (template_path.resolve() != (root / CANDIDATE_TEMPLATE_PATH).resolve()
            or template_sha256 != CANDIDATE_TEMPLATE_SHA256):
        raise metric.EvidenceError("Candidate template is not the reviewed V45 control")


def _validate_historical_source(root: Path) -> None:
    binding = _read_json(
        root, root / HISTORICAL_SOURCE_BINDING,
        HISTORICAL_SOURCE_BINDING_SHA256, "historical capture source binding",
    )
    if (set(binding) != {"original", "snapshot"}
            or binding.get("snapshot") != HISTORICAL_SOURCE.as_posix()):
        raise metric.EvidenceError("Historical capture source binding changed")
    original = metric.geometry._object(binding.get("original"), "historical source")
    if (original != {
            "path": "tools/GraphReader.SyntheticRuntimeEvidence/OfficialHeadTensorCapture.cs",
            "sha256": HISTORICAL_SOURCE_SHA256,
        }):
        raise metric.EvidenceError("Historical capture source identity changed")
    metric.geometry._read_exact(
        root, str(HISTORICAL_SOURCE), HISTORICAL_SOURCE_SHA256,
        "historical capture source snapshot",
    )


def _validate_capture_dependencies(
    root: Path, request: Mapping[str, Any], *, historical: bool,
) -> None:
    metric.geometry._descriptor(root, request.get("candidate"), "capture baseline candidate")
    detector = metric.geometry._object(request.get("detector"), "capture detector")
    if (detector.get("model_id") != "PP-OCRv5_mobile_det"
            or detector.get("model_version") != "5.0.0"
            or detector.get("model_sha256") != metric.geometry.PINNED_CAPTURE_MODEL_SHA256):
        raise metric.EvidenceError("Capture detector is not the pinned official model")
    metric.geometry._validate_capture_detector_model(
        root, detector.get("model_path"), detector.get("model_sha256")
    )
    metric.geometry._read_exact(
        root, detector.get("manifest_path"), detector.get("manifest_sha256"),
        "capture detector manifest",
    )
    native = metric.geometry._object(request.get("native"), "capture native")
    metric.geometry._read_historical_capture_artifact(
        root, native.get("path"), native.get("sha256"), "capture native"
    )
    for raw in metric.geometry._array(request.get("license_inputs"), "capture licenses"):
        row = metric.geometry._object(raw, "capture license")
        metric.geometry._read_historical_capture_artifact(
            root, row.get("path"), row.get("sha256"), "capture license"
        )
    if historical:
        _validate_historical_source(root)
        _validate_historical_assemblies(root, request)
    else:
        metric.geometry._descriptor(root, request.get("capture_source"), "capture source")
        metric.geometry._validate_assemblies(
            root, request.get("assemblies"), "capture assemblies"
        )


def _validate_historical_requests(
    root: Path,
    original_path: Path,
    original_sha256: str,
    derived_path: Path,
    derived_sha256: str,
) -> tuple[Mapping[str, Any], Mapping[str, Any], Mapping[str, Any]]:
    if original_sha256 != ORIGINAL_REQUEST_SHA256:
        raise metric.EvidenceError("Historical capture request is not frozen")
    original = _read_json(root, original_path, original_sha256, "historical capture request")
    derived = _read_json(root, derived_path, derived_sha256, "historical evaluation request")
    _validate_historical_request_delta(original, derived)
    if (original.get("schema") != metric.geometry.CAPTURE_REQUEST_SCHEMA
            or original.get("scope") != metric.geometry.production_head_inputs.CAPTURE_SCOPE
            or original.get("synthetic_only") is not True
            or original.get("private_data") is not False
            or original.get("sealed_data") is not False
            or original.get("truth_included") is not False
            or original.get("model_inference") is not False
            or original.get("production_approved") is not False):
        raise metric.EvidenceError("Historical capture request scope changed")
    binding = metric.geometry._object(original.get("binding"), "historical binding")
    metric.geometry._descriptor(root, binding, "historical binding")
    _validate_capture_dependencies(root, original, historical=True)
    panels = metric.geometry._runtime_panels(root, derived)
    return original, derived, panels


def _validate_historical_request_delta(
    original: Mapping[str, Any], derived: Mapping[str, Any],
) -> None:
    expected = deepcopy(original)
    capture_source = metric.geometry._object(
        expected.get("capture_source"), "historical capture source"
    )
    if capture_source.get("sha256") != HISTORICAL_SOURCE_SHA256:
        raise metric.EvidenceError("Historical capture source checksum changed")
    capture_source["path"] = HISTORICAL_SOURCE.as_posix()
    if derived != expected:
        raise metric.EvidenceError(
            "Historical evaluation request changed beyond the capture source path"
        )


def _supplemental_runtime_panels(
    root: Path, request: Mapping[str, Any],
) -> dict[str, Any]:
    reports = metric.geometry._array(request.get("reports"), "supplemental reports")
    if len(reports) != 1:
        raise metric.EvidenceError("Supplemental request must bind one report")
    descriptor = metric.geometry._object(reports[0], "supplemental report")
    if (set(descriptor) != {
            "split", "manifest_path", "manifest_sha256", "report_path", "report_sha256"
        } or descriptor.get("split") != "train"):
        raise metric.EvidenceError("Supplemental report binding changed")
    _, manifest_payload = metric.geometry._read_exact(
        root, descriptor["manifest_path"], descriptor["manifest_sha256"],
        "supplemental runtime manifest",
    )
    manifest = metric.geometry._json(manifest_payload, "supplemental runtime manifest")
    images = metric.geometry._array(manifest.get("images"), "supplemental images")
    if len(images) != 6:
        raise metric.EvidenceError("Supplemental source count changed")
    sources: dict[str, tuple[int, int]] = {}
    manifest_path = metric.geometry._inside(
        root, descriptor["manifest_path"], "supplemental manifest"
    )
    for raw in images:
        image = metric.geometry._object(raw, "supplemental image")
        source = metric.geometry._sha(image.get("image_sha256"), "supplemental source")
        if source in sources or image.get("split") != "train":
            raise metric.EvidenceError("Supplemental source identity changed")
        source_path = metric.geometry._inside(
            root, manifest_path.parent / str(image.get("image")), "supplemental source image"
        )
        if sha256(source_path.read_bytes()).hexdigest() != source:
            raise metric.EvidenceError("Supplemental source image bytes changed")
        sources[source] = (
            metric.geometry._integer(image.get("width"), "source width", minimum=1),
            metric.geometry._integer(image.get("height"), "source height", minimum=1),
        )
    report_path, runtime_payload = metric.geometry._read_exact(
        root, descriptor["report_path"], descriptor["report_sha256"],
        "supplemental runtime report",
    )
    runtime = metric.geometry._json(runtime_payload, "supplemental runtime report")
    cases = metric.geometry._array(runtime.get("cases"), "supplemental runtime cases")
    if (runtime.get("schema") != metric.geometry.RUNTIME_REPORT_SCHEMA
            or runtime.get("scope") != metric.geometry.RUNTIME_SCOPE
            or runtime.get("production_approved") is not False
            or runtime.get("input_manifest_sha256") != descriptor["manifest_sha256"]
            or runtime.get("failed") != 0 or runtime.get("failed_panels") != 0
            or runtime.get("completed") != 6 or runtime.get("completed_panels") != 6
            or runtime.get("count") != 6 or runtime.get("panel_count") != 6
            or len(cases) != 6):
        raise metric.EvidenceError("Supplemental runtime report changed")
    runtime_by_source: dict[str, Mapping[str, Any]] = {}
    for raw in cases:
        case = metric.geometry._object(raw, "supplemental runtime case")
        source = metric.geometry._sha(case.get("image_sha256"), "supplemental runtime source")
        panels = metric.geometry._array(case.get("panels"), "supplemental runtime panels")
        if (source in runtime_by_source or sources.get(source) != (
                case.get("width"), case.get("height"))
                or case.get("status") != "panels-completed" or len(panels) != 1):
            raise metric.EvidenceError("Supplemental runtime source changed")
        runtime_by_source[source] = metric.geometry._object(
            panels[0], "supplemental runtime panel"
        )
    if set(runtime_by_source) != set(sources):
        raise metric.EvidenceError("Supplemental runtime source inventory changed")

    result: dict[str, Any] = {}
    for raw in metric.geometry._array(request.get("panels"), "supplemental request panels"):
        panel = metric.geometry._object(raw, "supplemental request panel")
        panel_id = panel.get("panel_id")
        source = metric.geometry._sha(panel.get("source_sha256"), "supplemental panel source")
        runtime_panel = runtime_by_source.get(source)
        if (not isinstance(panel_id, str) or not panel_id or panel_id in result
                or runtime_panel is None or runtime_panel.get("panel_id") != panel_id
                or panel.get("split") != "train"):
            raise metric.EvidenceError("Supplemental panel identity changed")
        width = metric.geometry._integer(panel.get("width"), "panel width", minimum=1)
        height = metric.geometry._integer(panel.get("height"), "panel height", minimum=1)
        crop = tuple(metric.geometry._array(panel.get("crop"), "supplemental crop"))
        if len(crop) != 4 or any(type(item) is not int for item in crop):
            raise metric.EvidenceError("Supplemental crop changed")
        requested = metric.geometry._box(runtime_panel.get("requested_crop"), "requested crop")
        source_to_panel = metric.geometry._matrix(
            runtime_panel.get("source_to_panel_matrix"), "source-to-panel"
        )
        panel_to_source = metric.geometry._matrix(
            runtime_panel.get("panel_to_source_matrix"), "panel-to-source"
        )
        metric.geometry._validate_inverse(source_to_panel, panel_to_source)
        panel_sha = metric.geometry._sha(panel.get("panel_sha256"), "supplemental panel")
        gray_sha = metric.geometry._sha(
            panel.get("recorded_unmasked_gray_sha256"), "supplemental Gray8"
        )
        bgr_sha = metric.geometry._sha(
            panel.get("reconstructed_bgr_sha256"), "supplemental BGR24"
        )
        diagnostic = metric.geometry._object(
            runtime_panel.get("ocr_proposal_diagnostic"), "supplemental OCR diagnostic"
        )
        png = metric.geometry._object(panel.get("panel_png"), "supplemental PNG")
        runtime_png = metric.geometry._object(
            runtime_panel.get("panel_png"), "supplemental runtime PNG"
        )
        _, encoded = metric.geometry._read_exact(
            root, png.get("path"), png.get("sha256"), "supplemental panel PNG",
            png.get("byte_count"),
        )
        decoded_gray, decoded_bgr = metric.geometry.production_head_inputs._decode_production_pixels(
            encoded, width, height
        )
        source_width, source_height = sources[source]
        expected_forward = (
            1.0, 0.0, -float(crop[0]),
            0.0, 1.0, -float(crop[1]),
            0.0, 0.0, 1.0,
        )
        expected_inverse = (
            1.0, 0.0, float(crop[0]),
            0.0, 1.0, float(crop[1]),
            0.0, 0.0, 1.0,
        )
        if (crop[0] < 0 or crop[1] < 0
                or crop[0] + crop[2] > source_width
                or crop[1] + crop[3] > source_height
                or requested[0] < crop[0] or requested[1] < crop[1]
                or requested[0] + requested[2] > crop[0] + crop[2]
                or requested[1] + requested[3] > crop[1] + crop[3]
                or source_to_panel != expected_forward
                or panel_to_source != expected_inverse
                or runtime_panel.get("status") != "seed-completed"
                or runtime_panel.get("image_sha256") != panel_sha
                or runtime_panel.get("source_image_sha256") != source
                or runtime_panel.get("source_width") != source_width
                or runtime_panel.get("source_height") != source_height
                or runtime_panel.get("width") != width or runtime_panel.get("height") != height
                or tuple(metric.geometry._box(runtime_panel.get("crop"), "runtime crop")) != crop
                or diagnostic.get("unmasked_input_sha256") != gray_sha
                or png.get("sha256") != panel_sha
                or png.get("sha256") != runtime_png.get("sha256")
                or png.get("byte_count") != runtime_png.get("byte_count")
                or panel.get("bgr_identity_kind") != "reconstructed_from_authenticated_panel_png"
                or panel.get("report_path") != descriptor["report_path"]
                or panel.get("report_sha256") != descriptor["report_sha256"]
                or sha256(decoded_gray.tobytes(order="C")).hexdigest() != gray_sha
                or sha256(decoded_bgr.tobytes(order="C")).hexdigest() != bgr_sha):
            raise metric.EvidenceError("Supplemental panel pixels or geometry changed")
        result[panel_id] = metric.geometry._PanelIdentity(
            "train", source, source_width, source_height, panel_id, panel_sha,
            width, height, crop, requested, source_to_panel, panel_to_source,
            gray_sha, bgr_sha,
        )
    if len(result) != 6:
        raise metric.EvidenceError("Supplemental panel denominator changed")
    return result


def _validate_supplemental_request(
    root: Path, request_path: Path, request_sha256: str,
) -> tuple[Mapping[str, Any], Mapping[str, Any]]:
    if request_sha256 != SUPPLEMENTAL_REQUEST_SHA256:
        raise metric.EvidenceError("Supplemental request is not frozen")
    request = _read_json(root, request_path, request_sha256, "supplemental request")
    if (request.get("schema") != legend_bridge.REQUEST_SCHEMA
            or request.get("scope") != legend_bridge.CAPTURE_SCOPE
            or request.get("synthetic_only") is not True
            or request.get("private_data") is not False
            or request.get("sealed_data") is not False
            or request.get("truth_included") is not False
            or request.get("model_inference") is not False
            or request.get("production_approved") is not False):
        raise metric.EvidenceError("Supplemental request scope changed")
    metric.geometry._descriptor(root, request.get("binding"), "supplemental binding")
    _validate_capture_dependencies(root, request, historical=False)
    return request, _supplemental_runtime_panels(root, request)


def _validate_candidate_delta(
    root: Path, candidate_path: Path, candidate_sha256: str,
    template_path: Path, template_sha256: str,
) -> Mapping[str, Any]:
    _require_candidate_template_binding(root, template_path, template_sha256)
    candidate, _ = metric.geometry._validate_candidate(
        root, candidate_path, candidate_sha256
    )
    template, _ = metric.geometry._validate_candidate(
        root, template_path, template_sha256
    )
    detector = metric.geometry._object(candidate.get("detector"), "candidate detector")
    control = metric.geometry._object(template.get("detector"), "template detector")
    candidate_manifest = _read_json(
        root, metric.geometry._inside(root, detector["manifest_path"], "candidate manifest"),
        detector["manifest_sha256"], "candidate detector manifest",
    )
    control_manifest = _read_json(
        root, metric.geometry._inside(root, control["manifest_path"], "template manifest"),
        control["manifest_sha256"], "template detector manifest",
    )
    _validate_candidate_recipe_delta(
        candidate, template, candidate_manifest, control_manifest
    )
    _validate_modification_notice(
        root, candidate_path,
        metric.geometry._array(candidate.get("license_inputs"), "candidate licenses")[-1],
    )
    return candidate


def _validate_modification_notice(
    root: Path, candidate_path: Path, raw_notice: Any,
) -> None:
    notice = metric.geometry._object(raw_notice, "candidate modification notice")
    if set(notice) != {"path", "sha256"}:
        raise metric.EvidenceError("Candidate modification notice descriptor changed")
    notice_path, payload = metric.geometry._read_exact(
        root, notice.get("path"), notice.get("sha256"), "candidate modification notice"
    )
    if (notice_path.parent != candidate_path.resolve().parent
            or notice_path.name != MODIFICATION_NOTICE_NAME):
        raise metric.EvidenceError("Candidate modification notice location changed")
    try:
        text = payload.decode("utf-8")
    except UnicodeDecodeError as error:
        raise metric.EvidenceError("Candidate modification notice is not UTF-8") from error
    normalized = text.replace("\r\n", "\n")
    if "\r" in normalized or normalized != MODIFICATION_NOTICE_TEXT:
        raise metric.EvidenceError("Candidate modification notice content changed")


def _validate_combined_inventory(
    historical_panels: Mapping[str, Any], supplemental_panels: Mapping[str, Any],
) -> None:
    historical_ids = set(historical_panels)
    supplemental_ids = set(supplemental_panels)
    historical_sources = {panel.source_sha256 for panel in historical_panels.values()}
    supplemental_sources = {panel.source_sha256 for panel in supplemental_panels.values()}
    historical_train = {
        panel.source_sha256 for panel in historical_panels.values()
        if panel.split == "train"
    }
    historical_dev = {
        panel.source_sha256 for panel in historical_panels.values()
        if panel.split == "validation"
    }
    if (len(historical_panels) != 37 or len(historical_sources) != 23
            or sum(panel.split == "train" for panel in historical_panels.values()) != 28
            or sum(panel.split == "validation" for panel in historical_panels.values()) != 9
            or len(historical_train) != 20 or len(historical_dev) != 3
            or historical_train & historical_dev):
        raise metric.EvidenceError("Historical evaluation inventory changed")
    if (len(supplemental_panels) != 6 or len(supplemental_sources) != 6
            or any(panel.split != "train" for panel in supplemental_panels.values())):
        raise metric.EvidenceError("Supplemental evaluation inventory changed")
    if historical_ids & supplemental_ids or historical_sources & supplemental_sources:
        raise metric.EvidenceError("Historical and supplemental inventories overlap")
    if len(historical_ids | supplemental_ids) != 43:
        raise metric.EvidenceError("Combined panel inventory changed")
    if len(historical_sources | supplemental_sources) != 29:
        raise metric.EvidenceError("Combined source inventory changed")


def _validate_candidate_recipe_delta(
    candidate: Mapping[str, Any], template: Mapping[str, Any],
    candidate_manifest: Mapping[str, Any], control_manifest: Mapping[str, Any],
) -> None:
    if set(candidate) != set(template):
        raise metric.EvidenceError("Candidate shape changed from its template")
    for key in set(candidate) - {"detector", "license_inputs"}:
        if candidate.get(key) != template.get(key):
            raise metric.EvidenceError(f"Candidate changed non-detector field: {key}")
    candidate_licenses = metric.geometry._array(
        candidate.get("license_inputs"), "candidate licenses"
    )
    control_licenses = metric.geometry._array(
        template.get("license_inputs"), "template licenses"
    )
    if (len(candidate_licenses) != len(control_licenses) + 1
            or candidate_licenses[:-1] != control_licenses):
        raise metric.EvidenceError(
            "Candidate must preserve every original notice and append one modification notice"
        )
    detector = metric.geometry._object(candidate.get("detector"), "candidate detector")
    control = metric.geometry._object(template.get("detector"), "template detector")
    if (control.get("model_version") != PARENT_MODEL_VERSION
            or detector.get("model_version") != DERIVED_MODEL_VERSION
            or detector.get("model_sha256") == control.get("model_sha256")):
        raise metric.EvidenceError("Candidate detector identity delta is invalid")
    allowed = {
        "model_path", "model_id", "model_version", "model_sha256",
        "manifest_path", "manifest_sha256",
    }
    if any(detector.get(key) != control.get(key) for key in set(detector) - allowed):
        raise metric.EvidenceError("Candidate detector descriptor changed its recipe")
    if (control_manifest.get("model_version") != PARENT_MODEL_VERSION
            or candidate_manifest.get("model_version") != DERIVED_MODEL_VERSION):
        raise metric.EvidenceError("Candidate manifest version delta is invalid")
    manifest_delta = {"model_id", "model_version", "sha256", "files", "benchmarks"}
    candidate_recipe = {key: value for key, value in candidate_manifest.items()
                        if key not in manifest_delta}
    control_recipe = {key: value for key, value in control_manifest.items()
                      if key not in manifest_delta}
    if candidate_recipe != control_recipe:
        raise metric.EvidenceError(
            "Candidate detector manifest changed preprocessing, postprocessing, or structure"
        )


def _validate_supplemental_evaluation(
    root: Path, report_path: Path, report_sha256: str,
    request_path: Path, request_sha256: str,
    candidate_path: Path, candidate_sha256: str,
    panels: Mapping[str, Any], candidate: Mapping[str, Any],
) -> Any:
    report = _read_json(root, report_path, report_sha256, "supplemental evaluation")
    if (report.get("schema") != SUPPLEMENTAL_EVALUATION_SCHEMA
            or report.get("scope") != metric.geometry.CANDIDATE_SCOPE
            or report.get("development_only") is not True
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
        raise metric.EvidenceError("Supplemental evaluation scope changed")
    request_descriptor = metric.geometry._object(report.get("request"), "evaluation request")
    if (metric.geometry._inside(root, request_descriptor.get("path"), "evaluation request")
            != request_path.resolve() or request_descriptor.get("sha256") != request_sha256):
        raise metric.EvidenceError("Supplemental evaluation request binding changed")
    candidate_descriptor = metric.geometry._object(
        report.get("candidate"), "evaluation candidate"
    )
    detector = metric.geometry._object(candidate.get("detector"), "candidate detector")
    recognizer = metric.geometry._object(candidate.get("recognizer"), "candidate recognizer")
    expected_detector = {
        "task": "ocr_detection", "model_id": detector.get("model_id"),
        "version": detector.get("model_version"), "sha256": detector.get("model_sha256"),
        "manifest_path": metric.geometry._inside(
            root, detector.get("manifest_path"), "detector manifest"
        ).relative_to(root).as_posix(),
        "manifest_sha256": detector.get("manifest_sha256"),
    }
    expected_recognizer = {
        "task": "ocr_recognition", "model_id": recognizer.get("model_id"),
        "version": recognizer.get("model_version"), "sha256": recognizer.get("model_sha256"),
        "manifest_path": metric.geometry._inside(
            root, recognizer.get("manifest_path"), "recognizer manifest"
        ).relative_to(root).as_posix(),
        "manifest_sha256": recognizer.get("manifest_sha256"),
    }
    expected_adapter = (
        f"graphreader-ocr:{metric.geometry.COMPOSITION_VERSION}:"
        f"{str(detector.get('model_sha256'))[:12]}:{str(recognizer.get('model_sha256'))[:12]}:"
        f"{str(candidate.get('native_sha256'))[:12]}"
    )
    if (set(candidate_descriptor) != {
            "path", "sha256", "composition_version", "adapter_id", "configuration_scope",
            "detector", "recognizer", "native_sha256",
        } or metric.geometry._inside(root, candidate_descriptor.get("path"), "candidate")
            != candidate_path.resolve()
            or candidate_descriptor.get("sha256") != candidate_sha256
            or candidate_descriptor.get("composition_version") != metric.geometry.COMPOSITION_VERSION
            or candidate_descriptor.get("adapter_id") != expected_adapter
            or candidate_descriptor.get("configuration_scope") != "unapproved_frozen_candidate"
            or candidate_descriptor.get("native_sha256") != candidate.get("native_sha256")
            or candidate_descriptor.get("detector") != expected_detector
            or candidate_descriptor.get("recognizer") != expected_recognizer):
        raise metric.EvidenceError("Supplemental evaluation candidate changed")
    if (metric.geometry._validate_assemblies(
            root, report.get("execution_assemblies"), "evaluation assemblies")
            != metric.geometry._validate_assemblies(
                root, candidate.get("execution_assemblies"), "candidate assemblies")):
        raise metric.EvidenceError("Supplemental evaluation assemblies changed")
    records = metric.geometry._array(report.get("panels"), "supplemental evaluation panels")
    panel_count = metric.geometry._integer(report.get("panel_count"), "panel count")
    completed_count = metric.geometry._integer(report.get("completed_panel_count"), "completed count")
    failed_count = metric.geometry._integer(report.get("failed_panel_count"), "failed count")
    if (panel_count != 6 or len(records) != 6 or completed_count + failed_count != 6
            or report.get("status") != ("panels_completed" if failed_count == 0 else "failed")):
        raise metric.EvidenceError("Supplemental evaluation panel counts changed")

    raw_by_source: dict[str, list[Any]] = {}
    recognized_by_source: dict[str, list[Any]] = {}
    explicit = {"train": 0, "validation": 0}
    failed_raw = {"train": 0, "validation": 0}
    seen: set[str] = set()
    completed = failed = 0
    for raw_panel in records:
        record = metric.geometry._object(raw_panel, "supplemental evaluation panel")
        panel_id = record.get("panel_id")
        expected = panels.get(panel_id) if isinstance(panel_id, str) else None
        if expected is None or panel_id in seen:
            raise metric.EvidenceError("Supplemental evaluation panel is foreign or repeated")
        seen.add(panel_id)
        if (record.get("split") != "train" or record.get("source_sha256") != expected.source_sha256
                or record.get("source_width") != expected.source_width
                or record.get("source_height") != expected.source_height
                or record.get("panel_sha256") != expected.panel_sha256
                or record.get("width") != expected.width or record.get("height") != expected.height
                or metric.geometry._box(record.get("crop"), "evaluation crop") != expected.crop
                or metric.geometry._box(record.get("requested_crop"), "requested crop") != expected.requested_crop
                or metric.geometry._matrix(record.get("source_to_panel_matrix"), "source-to-panel")
                != expected.source_to_panel_matrix
                or metric.geometry._matrix(record.get("panel_to_source_matrix"), "panel-to-source")
                != expected.panel_to_source_matrix):
            raise metric.EvidenceError("Supplemental evaluation provenance changed")
        status = record.get("status")
        if status not in {"completed", "failed"}:
            raise metric.EvidenceError("Supplemental evaluation status changed")
        if status == "completed":
            completed += 1
            if (record.get("original_gray_sha256") != expected.gray_sha256
                    or record.get("original_bgr_sha256") != expected.bgr_sha256):
                raise metric.EvidenceError("Supplemental evaluation pixels changed")
        else:
            failed += 1
        raw_regions = metric.geometry._array(record.get("raw_detector_regions"), "raw regions")
        recognized_regions = metric.geometry._array(record.get("recognized_regions"), "recognized regions")
        raw_geometry: dict[str, tuple[tuple[float, float], ...]] = {}
        for raw_region in raw_regions:
            region = metric.geometry._object(raw_region, "raw region")
            region_id = region.get("region_id")
            if not isinstance(region_id, str) or not region_id or region_id in raw_geometry:
                raise metric.EvidenceError("Supplemental raw region identity changed")
            panel_points = metric.geometry._polygon(
                region.get("panel_polygon"), "raw panel polygon", expected.width, expected.height
            )
            source_points = metric.geometry._polygon(
                region.get("source_polygon"), "raw source polygon",
                expected.source_width, expected.source_height,
            )
            if (region.get("coordinate_space") != "source_original_pixels"
                    or not metric.geometry._same_points(
                        metric.geometry._mapped(panel_points, expected.panel_to_source_matrix),
                        source_points)):
                raise metric.EvidenceError("Supplemental raw geometry changed")
            raw_geometry[region_id] = source_points
            raw_by_source.setdefault(expected.source_sha256, []).append(
                metric.geometry._prediction(source_points)
            )
        recognized_ids: set[str] = set()
        for raw_region in recognized_regions:
            region = metric.geometry._object(raw_region, "recognized region")
            region_id = region.get("region_id")
            if (not isinstance(region_id, str) or region_id in recognized_ids
                    or region_id not in raw_geometry):
                raise metric.EvidenceError("Supplemental recognized identity changed")
            recognized_ids.add(region_id)
            panel_points = metric.geometry._polygon(
                region.get("panel_polygon"), "recognized panel polygon",
                expected.width, expected.height,
            )
            source_points = metric.geometry._polygon(
                region.get("source_polygon"), "recognized source polygon",
                expected.source_width, expected.source_height,
            )
            raw_match = next(
                item for item in raw_regions
                if metric.geometry._object(item, "raw region").get("region_id") == region_id
            )
            if (not metric.geometry._same_points(
                    panel_points, metric.geometry._polygon(
                        raw_match.get("panel_polygon"), "raw comparison polygon",
                        expected.width, expected.height))
                    or not metric.geometry._same_points(source_points, raw_geometry[region_id])
                    or region.get("coordinate_space") != "source_original_pixels"):
                raise metric.EvidenceError("Supplemental recognized geometry changed")
            recognized_by_source.setdefault(expected.source_sha256, []).append(
                metric.geometry._prediction(source_points)
            )
        if status == "completed":
            failures = metric.geometry._array(record.get("region_failures"), "region failures")
            failure_ids = [
                metric.geometry._object(item, "region failure").get("region_id")
                for item in failures
            ]
            if (any(not isinstance(item, str) or not item for item in failure_ids)
                    or len(failure_ids) != len(set(failure_ids))
                    or set(raw_geometry) != recognized_ids | set(failure_ids)
                    or recognized_ids & set(failure_ids)):
                raise metric.EvidenceError("Supplemental failure inventory changed")
            explicit["train"] += len(failure_ids)
        else:
            if recognized_regions:
                raise metric.EvidenceError("Failed supplemental panel retained recognition")
            failed_raw["train"] += len(raw_regions)
    if seen != set(panels) or completed != completed_count or failed != failed_count:
        raise metric.EvidenceError("Supplemental evaluation omitted a panel")
    return metric.geometry._ValidatedEvidence(
        report, candidate, panels,
        {key: tuple(value) for key, value in raw_by_source.items()},
        {key: tuple(value) for key, value in recognized_by_source.items()},
        explicit, failed_raw,
    )


def _saved_supplemental_truth(
    document: Mapping[str, Any], authenticated: Sequence[Any],
) -> tuple[Any, ...]:
    expected = {truth.truth_id: truth for truth in authenticated}
    result = []
    seen: set[str] = set()
    rows = metric.geometry._array(document.get("truths"), "supplemental truth")
    for raw in rows:
        row = metric.geometry._object(raw, "supplemental truth row")
        truth = expected.get(row.get("truth_id"))
        if (truth is None or truth.truth_id in seen
                or row.get("source_sha256") != truth.source_sha256
                or tuple(row.get("source_box_ltrb", ())) != tuple(truth.source_box)
                or row.get("role") != truth.role
                or not isinstance(row.get("text"), str) or not row["text"].strip()):
            raise metric.EvidenceError("Supplemental saved truth changed")
        seen.add(truth.truth_id)
        result.append(metric.FullTextTruth(
            truth.truth_id, truth.source_sha256, metric.Box(*truth.source_box),
            row["text"], truth.role, metric._canonical_role(truth.role),
        ))
    if len(result) != 153 or len(seen) != 153:
        raise metric.EvidenceError("Supplemental truth denominator changed")
    return tuple(result)


def _merge_predictions(*mappings: Mapping[str, tuple[Any, ...]]) -> dict[str, tuple[Any, ...]]:
    result: dict[str, tuple[Any, ...]] = {}
    for mapping in mappings:
        if set(result) & set(mapping):
            raise metric.EvidenceError("Prediction source cohorts overlap")
        result.update(mapping)
    return result


def _combined_evidence(historical: Any, supplemental: Any) -> Any:
    if set(historical.panels) & set(supplemental.panels):
        raise metric.EvidenceError("Evaluation panel cohorts overlap")
    return SimpleNamespace(
        report={"panels": [*historical.report["panels"], *supplemental.report["panels"]]},
        candidate=historical.candidate,
        panels={**historical.panels, **supplemental.panels},
        raw_by_source=_merge_predictions(
            historical.raw_by_source, supplemental.raw_by_source
        ),
        recognized_by_source=_merge_predictions(
            historical.recognized_by_source, supplemental.recognized_by_source
        ),
        explicit_region_failures={
            "train": historical.explicit_region_failures["train"]
                     + supplemental.explicit_region_failures["train"],
            "validation": historical.explicit_region_failures["validation"],
        },
        failed_panel_raw_regions={
            "train": historical.failed_panel_raw_regions["train"]
                     + supplemental.failed_panel_raw_regions["train"],
            "validation": historical.failed_panel_raw_regions["validation"],
        },
    )


def score(
    *,
    historical_capture_request_path: Path,
    historical_capture_request_sha256: str,
    historical_capture_report_path: Path,
    historical_capture_report_sha256: str,
    historical_evaluation_request_path: Path,
    historical_evaluation_request_sha256: str,
    supplemental_request_path: Path,
    supplemental_request_sha256: str,
    supplemental_capture_report_path: Path,
    supplemental_capture_report_sha256: str,
    historical_evaluation_path: Path,
    historical_evaluation_sha256: str,
    supplemental_evaluation_path: Path,
    supplemental_evaluation_sha256: str,
    candidate_path: Path,
    candidate_sha256: str,
    candidate_template_path: Path,
    candidate_template_sha256: str,
    output_path: Path,
    source_sha: str,
    repository_root: Path = ROOT,
) -> Mapping[str, Any]:
    root = Path(repository_root).resolve()
    started = time.perf_counter()
    if (sha256(Path(__file__).read_bytes()).hexdigest() != source_sha
            or sha256(Path(metric.__file__).read_bytes()).hexdigest() != METRIC_SHA256
            or sha256(Path(historical_truth.__file__).read_bytes()).hexdigest()
            != HISTORICAL_TRUTH_SHA256):
        raise metric.EvidenceError("Scorer or frozen metric source changed")
    output = Path(output_path).resolve()
    if not output.is_relative_to(root / "artifacts") or output.exists():
        raise metric.EvidenceError("Use a new scoring artifact")

    source_bindings = _validate_scoring_sources(root)
    candidate = _validate_candidate_delta(
        root, Path(candidate_path).resolve(), candidate_sha256,
        Path(candidate_template_path).resolve(), candidate_template_sha256,
    )
    original, derived, historical_panels = _validate_historical_requests(
        root, Path(historical_capture_request_path).resolve(),
        historical_capture_request_sha256,
        Path(historical_evaluation_request_path).resolve(),
        historical_evaluation_request_sha256,
    )
    if historical_capture_report_sha256 != ORIGINAL_CAPTURE_SHA256:
        raise metric.EvidenceError("Historical capture report is not frozen")
    metric.geometry._prevalidate_capture_report(
        root, Path(historical_capture_report_path).resolve(),
        historical_capture_report_sha256,
        Path(historical_capture_request_path).resolve(),
        historical_capture_request_sha256, original, historical_panels,
    )
    historical_evidence = metric.geometry._validate_evaluation(
        root, Path(historical_evaluation_path).resolve(), historical_evaluation_sha256,
        Path(historical_evaluation_request_path).resolve(),
        historical_evaluation_request_sha256,
        Path(candidate_path).resolve(), candidate_sha256,
        historical_panels, candidate,
    )
    supplemental_request, supplemental_panels = _validate_supplemental_request(
        root, Path(supplemental_request_path).resolve(), supplemental_request_sha256
    )
    _validate_combined_inventory(historical_panels, supplemental_panels)
    if (supplemental_capture_report_sha256 != SUPPLEMENTAL_CAPTURE_SHA256
            or Path(supplemental_capture_report_path).resolve()
            != (root / supplemental_head_features.CAPTURE_PATH).resolve()):
        raise metric.EvidenceError("Supplemental capture report is not frozen")
    _, authenticated_capture, _ = supplemental_head_features.authenticate_inputs(
        repository_root=root
    )
    if authenticated_capture != _read_json(
            root, Path(supplemental_capture_report_path).resolve(),
            supplemental_capture_report_sha256, "supplemental capture report"):
        raise metric.EvidenceError("Supplemental capture authentication changed")
    supplemental_evidence = _validate_supplemental_evaluation(
        root, Path(supplemental_evaluation_path).resolve(), supplemental_evaluation_sha256,
        Path(supplemental_request_path).resolve(), supplemental_request_sha256,
        Path(candidate_path).resolve(), candidate_sha256,
        supplemental_panels, candidate,
    )

    # Truth access begins only after both complete runtime/evaluation cohorts authenticate.
    binding = metric.geometry._object(original.get("binding"), "historical binding")
    runtime_reports = [bridge.RuntimeReportEvidence(
        row["split"], root / row["manifest_path"], row["manifest_sha256"],
        root / row["report_path"], row["report_sha256"],
    ) for row in original["reports"]]
    base_preparation = legend_bridge.prepare_historical_base_capture(
        root / binding["path"], binding["sha256"], runtime_reports,
        root / original["candidate"]["path"], original["candidate"]["sha256"],
        Path(historical_capture_request_path).resolve(), historical_capture_request_sha256,
        root / HISTORICAL_BINARY_ROOT, root / HISTORICAL_SOURCE,
        root / HISTORICAL_BINARY_MANIFEST, HISTORICAL_BINARY_MANIFEST_SHA256,
        root / HISTORICAL_SOURCE_BINDING, HISTORICAL_SOURCE_BINDING_SHA256,
        repository_root=root,
    )
    base_inputs = bridge.load_text_extent_head_inputs(
        base_preparation, Path(historical_capture_report_path).resolve(),
        historical_capture_report_sha256, repository_root=root,
    )
    supplemental_report = metric.geometry._object(
        supplemental_request["reports"][0], "supplemental runtime report"
    )
    supplemental_preparation = legend_bridge.prepare_legend_coverage_capture_request(
        base_preparation,
        root / supplemental_request["binding"]["path"],
        supplemental_request["binding"]["sha256"],
        root / SUPPLEMENTAL_TRUTH_PATH, SUPPLEMENTAL_TRUTH_SHA256,
        root / supplemental_report["manifest_path"], supplemental_report["manifest_sha256"],
        root / supplemental_report["report_path"], supplemental_report["report_sha256"],
        root / supplemental_request["candidate"]["path"],
        supplemental_request["candidate"]["sha256"],
        repository_root=root,
        capture_binary_root=(root / supplemental_request["assemblies"][0]["path"]).parent,
        capture_source_path=root / supplemental_request["capture_source"]["path"],
    )
    combined_inputs = legend_bridge.load_legend_coverage_head_inputs(
        base_inputs, supplemental_preparation,
        Path(supplemental_capture_report_path).resolve(),
        supplemental_capture_report_sha256, repository_root=root,
    )
    preflight = historical_truth._read(root, binding)
    historical_train = historical_truth._saved_train_truth(
        historical_truth._read(root, preflight["train_text_truth"]), base_inputs.train
    )
    supplemental_document = _read_json(
        root, root / SUPPLEMENTAL_TRUTH_PATH,
        SUPPLEMENTAL_TRUTH_SHA256, "supplemental saved truth",
    )
    supplemental_ids = {
        row["truth_id"] for row in supplemental_document["truths"]
    }
    supplemental_authenticated = tuple(
        truth for truth in combined_inputs.train.source_truths
        if truth.truth_id in supplemental_ids
    )
    supplemental_train = _saved_supplemental_truth(
        supplemental_document, supplemental_authenticated
    )
    dev = historical_truth._fixed_dev_truth(root, preflight)
    if (len(historical_train) != 709 or len(supplemental_train) != 153
            or len(dev) != 183 or sum(len(item.text) for item in dev) != 1019):
        raise metric.EvidenceError("Full OCR truth denominator changed")
    combined_train = (*historical_train, *supplemental_train)

    historical_recognized = metric._recognized_predictions(historical_evidence)
    supplemental_recognized = metric._recognized_predictions(supplemental_evidence)
    train_sources = {truth.source_sha256 for truth in historical_train}
    dev_sources = {truth.source_sha256 for truth in dev}
    supplemental_sources = {truth.source_sha256 for truth in supplemental_train}
    historical_train_predictions = {
        key: value for key, value in historical_recognized.items() if key in train_sources
    }
    dev_predictions = {
        key: value for key, value in historical_recognized.items() if key in dev_sources
    }
    supplemental_predictions = {
        key: value for key, value in supplemental_recognized.items()
        if key in supplemental_sources
    }
    combined_train_predictions = _merge_predictions(
        historical_train_predictions, supplemental_predictions
    )
    metrics = {
        "train": metric._score_split(combined_train, combined_train_predictions),
        "validation": metric._score_split(dev, dev_predictions),
    }
    cohorts = {
        "historical_train": metric._score_split(
            historical_train, historical_train_predictions
        ),
        "supplemental_train": metric._score_split(
            supplemental_train, supplemental_predictions
        ),
    }
    combined_evidence = _combined_evidence(historical_evidence, supplemental_evidence)
    raw, recognized_geometry, failures = metric._geometry_and_failure_metrics(
        combined_evidence, {"train": combined_train, "validation": dev}
    )
    historical_raw, historical_recognized_geometry, historical_failures = (
        metric._geometry_and_failure_metrics(
            historical_evidence,
            {"train": historical_train, "validation": dev},
        )
    )
    supplemental_raw, supplemental_recognized_geometry, supplemental_failures = (
        metric._geometry_and_failure_metrics(
            supplemental_evidence, {"train": supplemental_train}
        )
    )
    bars_sha, _, _ = metric.geometry._load_acceptance_bar(root)
    result = {
        "schema": OUTPUT_SCHEMA,
        "status": "diagnostic_only_unapproved",
        "synthetic_only": True,
        "private_data": False,
        "sealed_data": False,
        "optimizer_steps": 0,
        "production_approval": False,
        "release_eligible": False,
        "truth_isolation": {
            "runtime_received_truth": False,
            "all_runtime_candidate_request_pixel_polygon_and_failure_evidence_authenticated_before_truth_access": True,
            "historical_train_truth": "saved_preflight_text_and_authenticated_geometry",
            "supplemental_train_truth": "saved_project_owned_text_and_authenticated_geometry",
            "dev_truth": "fixed_dev_renderer_authenticated_against_historical_rasters_and_geometry",
        },
        "inputs": {
            "historical_capture_request": _descriptor(
                root, Path(historical_capture_request_path), historical_capture_request_sha256
            ),
            "historical_capture_report": _descriptor(
                root, Path(historical_capture_report_path), historical_capture_report_sha256
            ),
            "historical_evaluation_request": _descriptor(
                root, Path(historical_evaluation_request_path),
                historical_evaluation_request_sha256,
            ),
            "supplemental_request": _descriptor(
                root, Path(supplemental_request_path), supplemental_request_sha256
            ),
            "supplemental_capture_report": _descriptor(
                root, Path(supplemental_capture_report_path), supplemental_capture_report_sha256
            ),
            "historical_evaluation": _descriptor(
                root, Path(historical_evaluation_path), historical_evaluation_sha256
            ),
            "supplemental_evaluation": _descriptor(
                root, Path(supplemental_evaluation_path), supplemental_evaluation_sha256
            ),
            "candidate": _descriptor(root, Path(candidate_path), candidate_sha256),
            "candidate_template": _descriptor(
                root, Path(candidate_template_path), candidate_template_sha256
            ),
            "input_adapter": _descriptor(root, Path(__file__), source_sha),
            "evaluator_sha256": METRIC_SHA256,
            "source_bindings": source_bindings,
            "reviewed_evaluator_source_substitution": {
                "path": CURRENT_EVALUATOR_SOURCE,
                "frozen_metric_sha256": metric.SOURCE_BINDINGS[CURRENT_EVALUATOR_SOURCE],
                "executed_sha256": CURRENT_EVALUATOR_SOURCE_SHA256,
                "all_other_frozen_metric_source_bindings_unchanged": True,
            },
        },
        "candidate_delta": {
            "template_composition_version": metric.geometry.COMPOSITION_VERSION,
            "parent_model_version": PARENT_MODEL_VERSION,
            "derived_model_version": DERIVED_MODEL_VERSION,
            "only_detector_identity_version_files_benchmark_fields_and_required_notice_differ": True,
            "one_exact_modification_notice_appended": True,
            "all_original_license_and_notice_inputs_preserved": True,
            "preprocessing_and_postprocessing_unchanged": True,
        },
        "metrics": metrics,
        "cohort_metrics": cohorts,
        "raw_detector_geometry": raw,
        "successfully_recognized_region_geometry": recognized_geometry,
        "recognition_failures": failures,
        "cohort_geometry": {
            "historical": {
                "raw_detector_geometry": historical_raw,
                "successfully_recognized_region_geometry": historical_recognized_geometry,
                "recognition_failures": historical_failures,
            },
            "supplemental": {
                "raw_detector_geometry": supplemental_raw,
                "successfully_recognized_region_geometry": supplemental_recognized_geometry,
                "recognition_failures": supplemental_failures,
            },
        },
        "acceptance_bar_reference": {
            "path": metric.geometry.ACCEPTANCE_BARS_PATH.as_posix(),
            "sha256": bars_sha,
            "scorer_does_not_select_or_approve_a_candidate": True,
        },
        "integrity": {
            "source_count": 29,
            "panel_count": 43,
            "full_source_truth_count": 1045,
            "train": {"source_count": 26, "panel_count": 34, "truth_count": 862},
            "historical_train": {"source_count": 20, "panel_count": 28, "truth_count": 709},
            "supplemental_train": {"source_count": 6, "panel_count": 6, "truth_count": 153},
            "validation": {
                "source_count": 3, "panel_count": 9, "truth_count": 183,
                "truth_character_count": 1019,
            },
            "failed_panels_remain_in_full_source_denominator": True,
        },
        "elapsed_milliseconds": (time.perf_counter() - started) * 1000,
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(result, stream, indent=2, allow_nan=False)
        stream.write("\n")
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    pairs = (
        "historical-capture-request", "historical-capture-report",
        "historical-evaluation-request", "supplemental-request",
        "supplemental-capture-report", "historical-evaluation",
        "supplemental-evaluation", "candidate", "candidate-template",
    )
    for name in pairs:
        parser.add_argument(f"--{name}", type=Path, required=True)
        parser.add_argument(f"--{name}-sha256", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--source-sha", required=True)
    arguments = parser.parse_args()
    values = vars(arguments)
    result = score(
        historical_capture_request_path=values["historical_capture_request"],
        historical_capture_request_sha256=values["historical_capture_request_sha256"],
        historical_capture_report_path=values["historical_capture_report"],
        historical_capture_report_sha256=values["historical_capture_report_sha256"],
        historical_evaluation_request_path=values["historical_evaluation_request"],
        historical_evaluation_request_sha256=values["historical_evaluation_request_sha256"],
        supplemental_request_path=values["supplemental_request"],
        supplemental_request_sha256=values["supplemental_request_sha256"],
        supplemental_capture_report_path=values["supplemental_capture_report"],
        supplemental_capture_report_sha256=values["supplemental_capture_report_sha256"],
        historical_evaluation_path=values["historical_evaluation"],
        historical_evaluation_sha256=values["historical_evaluation_sha256"],
        supplemental_evaluation_path=values["supplemental_evaluation"],
        supplemental_evaluation_sha256=values["supplemental_evaluation_sha256"],
        candidate_path=values["candidate"],
        candidate_sha256=values["candidate_sha256"],
        candidate_template_path=values["candidate_template"],
        candidate_template_sha256=values["candidate_template_sha256"],
        output_path=arguments.output,
        source_sha=arguments.source_sha,
    )
    print(json.dumps({"status": result["status"], "validation": result["metrics"]["validation"]}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
