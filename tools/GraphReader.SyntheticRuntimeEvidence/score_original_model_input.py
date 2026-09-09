# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Score the authenticated original-input OCR detector experiment.

The runtime exchange remains annotation-free. This evaluator authenticates the
reviewed train/dev protocol, candidate, execution files, source/crop bytes, and
both runtime reports before regenerating synthetic truth. It then uses the
existing IoU 0.5 maximum-cardinality scorer for raw expanded detector regions,
consensus-selected geometry, and final persisted OCR regions.
"""

from __future__ import annotations

import argparse
from copy import deepcopy
import json
from pathlib import Path
import sys
import time
from typing import Any, Mapping, Sequence
from uuid import UUID

REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
if str(REPOSITORY_ROOT) not in sys.path:
    sys.path.insert(0, str(REPOSITORY_ROOT))

import score_db_geometry_observation as db_geometry
import score_family_ocr as family_ocr
from ml.ocr.component_region_detector_v6.dataset import Component


PROTOCOL_PATH = Path("ml/ocr/official_bakeoff/original_model_input_dev_protocol.json")
EXPECTED_PROTOCOL_SHA256 = "ecba1f8ca3c1625c0b47deb6847a7fe9ea9d90e595b7ae8fbd582b39d9c10706"
PROTOCOL_KEYS = {
    "evidence_policy", "hypothesis", "isolated_change", "split_identities",
    "metric", "acceptance_bar", "budget",
}
IDENTITY_KEYS = {
    "train_manifest", "dev_manifest", "baseline_candidate",
    "baseline_train_report", "baseline_dev_report",
}
OUTPUT_SCHEMA = "graphreader.synthetic-original-model-input-score.v1"
EXPECTED_TRUTH_COUNTS = {"train": 146, "validation": 183}
EXPECTED_TOTAL_TRUTHS = 329
STAGES = ("raw_expanded", "consensus", "final_ocr_regions")

EvidenceError = family_ocr.EvidenceError


def _require_mapping(value: Any, label: str) -> Mapping[str, Any]:
    return db_geometry._require_mapping(value, label)


def _require_list(value: Any, label: str) -> list[Any]:
    return db_geometry._require_list(value, label)


def _load_protocol(
    protocol_path: Path,
) -> tuple[dict[str, Any], bytes, dict[str, tuple[Path, str]]]:
    expected_path = (REPOSITORY_ROOT / PROTOCOL_PATH).resolve()
    if protocol_path.resolve() != expected_path:
        raise EvidenceError("original-input scoring requires the reviewed repository protocol path")
    protocol, payload = family_ocr._load_object(expected_path, "original-input protocol")
    if family_ocr._sha256_bytes(payload) != EXPECTED_PROTOCOL_SHA256:
        raise EvidenceError("original-input protocol bytes differ from the reviewed protocol")
    family_ocr._require_exact_keys(protocol, PROTOCOL_KEYS, "original-input protocol")
    if (
        protocol["evidence_policy"] != "ml/policy/evidence-policy.json"
        or not isinstance(protocol["hypothesis"], str) or not protocol["hypothesis"].strip()
        or not isinstance(protocol["isolated_change"], str) or not protocol["isolated_change"].strip()
        or not isinstance(protocol["metric"], str) or not protocol["metric"].strip()
        or not isinstance(protocol["acceptance_bar"], str) or not protocol["acceptance_bar"].strip()
    ):
        raise EvidenceError("original-input protocol metadata is invalid")
    budget = _require_mapping(protocol["budget"], "original-input protocol budget")
    family_ocr._require_exact_keys(
        budget, {"train_dev_runs", "sealed_runs"}, "original-input protocol budget")
    if budget["train_dev_runs"] != "unlimited" or budget["sealed_runs"] != 0:
        raise EvidenceError("original-input protocol cannot authorize sealed evidence")
    identities = _require_mapping(protocol["split_identities"], "original-input split identities")
    family_ocr._require_exact_keys(identities, IDENTITY_KEYS, "original-input split identities")
    bound = {
        key: db_geometry._validate_bound_file(identities[key], f"original-input {key}")
        for key in sorted(IDENTITY_KEYS)
    }
    return protocol, payload, bound


def _validate_model_descriptor(
    candidate: Mapping[str, Any],
    task_name: str,
    expected_task: str,
) -> dict[str, str]:
    descriptor = _require_mapping(candidate.get(task_name), f"candidate {task_name}")
    required = {
        "model_path", "model_id", "model_version", "model_sha256",
        "manifest_path", "manifest_sha256",
    }
    if not required.issubset(descriptor):
        raise EvidenceError(f"candidate {task_name} descriptor is incomplete")
    model_sha = family_ocr._require_sha256(
        descriptor["model_sha256"], f"candidate {task_name} model SHA-256")
    manifest_sha = family_ocr._require_sha256(
        descriptor["manifest_sha256"], f"candidate {task_name} manifest SHA-256")
    db_geometry._external_file(descriptor["model_path"], model_sha, f"candidate {task_name} model")
    manifest_path = db_geometry._external_file(
        descriptor["manifest_path"], manifest_sha, f"candidate {task_name} manifest")
    manifest, _ = family_ocr._load_object(manifest_path, f"candidate {task_name} manifest")
    model_id = descriptor.get("model_id")
    model_version = descriptor.get("model_version")
    license_record = _require_mapping(manifest.get("license"), f"candidate {task_name} license")
    providers = _require_list(manifest.get("providers"), f"candidate {task_name} providers")
    if (
        not isinstance(model_id, str) or not model_id
        or not isinstance(model_version, str) or not model_version
        or manifest.get("model_id") != model_id
        or manifest.get("model_version") != model_version
        or manifest.get("task") != expected_task
        or family_ocr._require_sha256(
            manifest.get("sha256"), f"candidate {task_name} manifest payload SHA-256") != model_sha
        or license_record.get("spdx") != "Apache-2.0"
        or license_record.get("reviewed") is not True
        or manifest.get("commercial_use") is not True
        or manifest.get("redistribution") is not True
        or "cpu" not in providers
    ):
        raise EvidenceError(f"candidate {task_name} manifest identity or reviewed license is invalid")
    return {
        "model_id": model_id,
        "model_version": model_version,
        "model_sha256": model_sha,
        "manifest_path": str(descriptor["manifest_path"]),
        "manifest_sha256": manifest_sha,
    }


def _validate_complete_candidate(candidate_path: Path, expected_hash: str) -> tuple[dict[str, Any], dict[str, Any]]:
    candidate_path = family_ocr._require_artifact_path(
        candidate_path, REPOSITORY_ROOT, "runtime candidate")
    summary = db_geometry._validate_candidate(candidate_path, expected_hash)
    candidate, payload = family_ocr._load_object(candidate_path, "runtime candidate")
    if family_ocr._sha256_bytes(payload) != expected_hash:
        raise EvidenceError("runtime candidate bytes differ from the caller-bound SHA-256")
    license_inputs = _require_list(candidate.get("license_inputs"), "candidate license inputs")
    if not license_inputs:
        raise EvidenceError("candidate must bind reviewed license inputs")
    seen_license_hashes: set[str] = set()
    for index, raw in enumerate(license_inputs):
        record = _require_mapping(raw, f"candidate license input {index}")
        family_ocr._require_exact_keys(record, {"path", "sha256"}, f"candidate license input {index}")
        expected_license_hash = family_ocr._require_sha256(
            record["sha256"], f"candidate license input {index} SHA-256")
        if expected_license_hash in seen_license_hashes:
            raise EvidenceError("candidate license inputs must be unique")
        seen_license_hashes.add(expected_license_hash)
        db_geometry._external_file(
            record["path"], expected_license_hash, f"candidate license input {index}")
    summary["detector"] = _validate_model_descriptor(candidate, "detector", "ocr_detection")
    summary["recognizer"] = _validate_model_descriptor(candidate, "recognizer", "ocr_recognition")
    return candidate, summary


def _validate_isolated_candidate(
    baseline_candidate: Mapping[str, Any],
    original_candidate: Mapping[str, Any],
    protocol_sha256: str,
) -> None:
    expected_protocol = {"path": PROTOCOL_PATH.as_posix(), "sha256": protocol_sha256}
    if original_candidate.get("ocr_model_input") != "original":
        raise EvidenceError("experimental candidate must select original OCR model input")
    if original_candidate.get("model_input_protocol") != expected_protocol:
        raise EvidenceError("experimental candidate does not bind the reviewed model-input protocol")
    normalized = deepcopy(dict(original_candidate))
    normalized.pop("ocr_model_input", None)
    normalized.pop("model_input_protocol", None)
    if normalized != baseline_candidate:
        raise EvidenceError("experimental candidate changes more than the reviewed model-input fields")


def _runtime_models(
    panel: Mapping[str, Any], candidate: Mapping[str, Any], selected_input_sha256: str,
    structure_input_sha256: str, mode: str, recognition_executed: bool,
) -> None:
    configured = _require_mapping(panel.get("ocr_configured_models"), "configured OCR models")
    models = _require_list(configured.get("models"), "configured OCR models")
    if configured.get("scope") != "unapproved_local_synthetic_candidate" or len(models) != 2:
        raise EvidenceError("configured OCR models must be the unapproved detector and recognizer pair")
    expected_by_task = {
        "ocr_detection": candidate["detector"],
        "ocr_recognition": candidate["recognizer"],
    }
    configured_tasks: set[str] = set()
    for raw in models:
        model = _require_mapping(raw, "configured OCR model")
        task = model.get("task")
        if task not in expected_by_task or task in configured_tasks:
            raise EvidenceError("configured OCR model tasks must be distinct detector and recognizer")
        configured_tasks.add(str(task))
        expected = expected_by_task[str(task)]
        if (
            model.get("model_id") != expected["model_id"]
            or model.get("version") != expected["model_version"]
            or family_ocr._require_sha256(model.get("sha256"), "configured model SHA-256")
            != expected["model_sha256"]
            or model.get("execution_provider") != "cpu"
        ):
            raise EvidenceError("configured OCR model identity differs from the candidate")

    evidence_items = _require_list(panel.get("ocr_models"), "executed OCR models")
    expected_execution_tasks = {"ocr_detection", "ocr_recognition"} if recognition_executed else {"ocr_detection"}
    if len(evidence_items) != len(expected_execution_tasks):
        raise EvidenceError("executed OCR evidence does not match the detector/recognizer execution path")
    executed_tasks: set[str] = set()
    panel_id = str(panel.get("panel_id"))
    panel_sha = family_ocr._require_sha256(panel.get("image_sha256"), "panel SHA-256")
    for raw in evidence_items:
        item = _require_mapping(raw, "executed OCR model")
        task = item.get("task")
        envelope = _require_mapping(item.get("envelope"), "executed OCR model envelope")
        model = _require_mapping(envelope.get("model"), "executed OCR model identity")
        if task not in expected_by_task or task in executed_tasks:
            raise EvidenceError("executed OCR tasks must be distinct detector and recognizer")
        executed_tasks.add(str(task))
        expected = expected_by_task[str(task)]
        try:
            UUID(str(envelope.get("run_id")))
            UUID(str(envelope.get("project_id")))
            UUID(str(envelope.get("panel_id")))
        except ValueError as exception:
            raise EvidenceError("executed OCR envelope identities must be UUIDs") from exception
        if (
            envelope.get("contract_version") != 1
            or envelope.get("stage") != "ocr"
            or envelope.get("coordinate_space") != "original_pixels"
            or str(envelope.get("panel_id")) != panel_id
            or family_ocr._require_sha256(envelope.get("input_sha256"), "executed OCR input SHA-256") != panel_sha
            or model.get("model_id") != expected["model_id"]
            or model.get("version") != expected["model_version"]
            or family_ocr._require_sha256(model.get("sha256"), "executed model SHA-256")
            != expected["model_sha256"]
            or model.get("provider") != "cpu"
        ):
            raise EvidenceError("executed OCR envelope differs from the candidate or panel")
        warnings = _require_list(envelope.get("warnings"), "executed OCR warnings")
        if mode == "original":
            required_warnings = {
                "ocr_detector_model_input_original",
                f"ocr_detector_model_input_sha256:{selected_input_sha256}",
                "ocr_detector_structure_input_axis_masked",
                f"ocr_detector_structure_input_sha256:{structure_input_sha256}",
            }
        else:
            required_warnings = {
                "ocr_detector_axis_geometry_mask_applied",
                f"ocr_detector_input_sha256:{selected_input_sha256}",
            }
        if not required_warnings.issubset(set(warnings)):
            raise EvidenceError("executed OCR warnings do not prove the selected detector input")
    if executed_tasks != expected_execution_tasks:
        raise EvidenceError("executed OCR evidence does not match the detector/recognizer execution path")


def _region_components(
    regions: Any,
    panel: Mapping[str, Any],
    label: str,
) -> tuple[Component, ...]:
    raw_regions = _require_list(regions, label)
    synthetic_case = {
        "status": "seed-completed",
        "width": panel["width"],
        "height": panel["height"],
        "ocr": {
            "contract_version": 1,
            "stage": "ocr",
            "coordinate_space": "original_pixels",
            "succeeded": True,
            "failure": None,
            "input_sha256": panel["image_sha256"],
            "regions": raw_regions,
        },
    }
    crop = _require_mapping(panel["crop"], "panel crop")
    return family_ocr._prediction_regions(
        synthetic_case,
        {"width": panel["width"], "height": panel["height"], "image_sha256": panel["image_sha256"]},
        offset_x=int(crop["x"]), offset_y=int(crop["y"]), require_bounded=True,
    )


def _final_components(panel: Mapping[str, Any]) -> tuple[Component, ...]:
    crop = _require_mapping(panel["crop"], "panel crop")
    return family_ocr._prediction_regions(
        panel,
        {"width": panel["width"], "height": panel["height"], "image_sha256": panel["image_sha256"]},
        offset_x=int(crop["x"]), offset_y=int(crop["y"]), require_bounded=True,
    )


def _crop_identity(panel: Mapping[str, Any]) -> tuple[float, float, float, float]:
    crop = _require_mapping(panel.get("crop"), "panel crop")
    return tuple(
        family_ocr._require_number(crop.get(field), f"panel crop {field}")
        for field in ("x", "y", "width", "height")
    )  # type: ignore[return-value]


def _panel_map(source: Mapping[str, Any], label: str) -> dict[tuple[float, float, float, float], Mapping[str, Any]]:
    panels = _require_list(source.get("panels"), f"{label} panels")
    result: dict[tuple[float, float, float, float], Mapping[str, Any]] = {}
    for panel in panels:
        record = _require_mapping(panel, f"{label} panel")
        identity = _crop_identity(record)
        if identity in result:
            raise EvidenceError(f"{label} contains duplicate panel crop identities")
        result[identity] = record
    return result


def _validate_panel_pair(
    baseline: Mapping[str, Any],
    original: Mapping[str, Any],
    baseline_candidate: Mapping[str, Any],
    original_candidate: Mapping[str, Any],
) -> tuple[dict[str, tuple[Component, ...]], dict[str, tuple[Component, ...]], bool]:
    provenance_fields = (
        "image_sha256", "width", "height", "source_image_sha256", "source_width",
        "source_height", "crop", "requested_crop", "source_to_panel_matrix",
        "panel_to_source_matrix", "panel_png", "source_gray", "detector_input_sha256",
        "detector_bgr_sha256", "pre_ocr_diagnostic",
    )
    if any(baseline.get(field) != original.get(field) for field in provenance_fields):
        raise EvidenceError("baseline and original-input panels differ in source, crop, or structural input")
    baseline_stages, baseline_diagnostic, baseline_masked, baseline_original = _validate_completed_panel(
        baseline, baseline_candidate, "axis-masked") if baseline.get("status") == "seed-completed" else (
            {stage: () for stage in STAGES}, None, None, None)
    original_stages, original_diagnostic, original_masked, original_original = _validate_completed_panel(
        original, original_candidate, "original") if original.get("status") == "seed-completed" else (
            {stage: () for stage in STAGES}, None, None, None)
    if baseline_diagnostic is None or original_diagnostic is None:
        return baseline_stages, original_stages, False
    for diagnostic in (baseline_diagnostic, original_diagnostic):
        if (
            diagnostic.get("coordinate_space") != "original_pixels"
            or diagnostic.get("used_as_accepted_evidence") is not False
        ):
            raise EvidenceError("OCR proposal diagnostics must remain unapproved original-pixel evidence")
    structural_fields = (
        "model_regions", "unmasked_model_regions", "component_regions",
        "detector_input_sha256", "unmasked_input_sha256",
    )
    if any(baseline_diagnostic.get(field) != original_diagnostic.get(field) for field in structural_fields):
        raise EvidenceError("original-input execution changed a learned or structural diagnostic output")
    if baseline_masked != original_masked or baseline_original != original_original:
        raise EvidenceError("baseline and original-input reports bind different detector inputs")
    return baseline_stages, original_stages, original_original != original_masked


def _validate_completed_panel(
    panel: Mapping[str, Any],
    candidate: Mapping[str, Any],
    mode: str,
) -> tuple[dict[str, tuple[Component, ...]], Mapping[str, Any], str, str]:
    diagnostic = _require_mapping(panel.get("ocr_proposal_diagnostic"), "OCR proposal diagnostic")
    if (
        diagnostic.get("coordinate_space") != "original_pixels"
        or diagnostic.get("used_as_accepted_evidence") is not False
    ):
        raise EvidenceError("OCR proposal diagnostics must remain unapproved original-pixel evidence")
    masked_sha = family_ocr._require_sha256(
        diagnostic.get("detector_input_sha256"), "axis-masked detector input SHA-256")
    original_sha = family_ocr._require_sha256(
        diagnostic.get("unmasked_input_sha256"), "original detector input SHA-256")
    source_gray = _require_mapping(panel.get("source_gray"), "panel source Gray8")
    if (
        masked_sha != family_ocr._require_sha256(
            panel.get("detector_input_sha256"), "panel detector input SHA-256")
        or original_sha != family_ocr._require_sha256(
            source_gray.get("sha256"), "panel source Gray8 SHA-256")
    ):
        raise EvidenceError("recorded original and axis-masked detector inputs are inconsistent")
    selected_field = "model_regions" if mode == "axis-masked" else "unmasked_model_regions"
    selected_input_sha = masked_sha if mode == "axis-masked" else original_sha
    selected_regions = _require_list(diagnostic.get(selected_field), f"{mode} raw regions")
    ocr = _require_mapping(panel.get("ocr"), f"{mode} OCR output")
    regions = _require_list(ocr.get("regions"), f"{mode} final OCR regions")
    cache = _require_mapping(ocr.get("cache"), f"{mode} OCR cache")
    failures = _require_list(ocr.get("region_failures"), f"{mode} OCR region failures")
    if family_ocr._require_int(cache.get("crop_count"), f"{mode} OCR crop count", minimum=0) != len(regions):
        raise EvidenceError(f"{mode} OCR crop count differs from persisted final regions")
    if failures:
        raise EvidenceError(f"{mode} OCR region failures prevent complete consensus geometry scoring")
    selected_by_id = {
        str(_require_mapping(region, f"{mode} selected region").get("region_id")): region
        for region in selected_regions
    }
    if len(selected_by_id) != len(selected_regions):
        raise EvidenceError(f"{mode} selected detector region identities are not unique")
    for region in regions:
        final = _require_mapping(region, f"{mode} final OCR region")
        selected_region = selected_by_id.get(str(final.get("region_id")))
        if selected_region is None or _require_mapping(
            selected_region, f"{mode} selected detector region").get("polygon") != final.get("polygon"):
            raise EvidenceError(f"{mode} final OCR geometry is not selected raw detector geometry")
    _runtime_models(
        panel, candidate, selected_input_sha, masked_sha, mode, len(regions) > 0)
    final_components = _final_components(panel)
    return ({
        "raw_expanded": _region_components(selected_regions, panel, f"{mode} raw regions"),
        "consensus": final_components,
        "final_ocr_regions": final_components,
    }, diagnostic, masked_sha, original_sha)


def _validate_report(
    split_name: str,
    manifest_path: Path,
    manifest_sha256: str,
    report_path: Path,
    report_sha256: str,
    candidate: Mapping[str, Any],
    expected_model_input: str,
    protocol_sha256: str,
    executed_assemblies: tuple[tuple[str, str], ...] | None,
) -> tuple[list[dict[str, Any]], dict[str, Mapping[str, Any]], dict[str, Any], bytes, int, tuple[tuple[str, str], ...]]:
    manifest, manifest_bytes = family_ocr._load_object(manifest_path, f"{split_name} input manifest")
    if family_ocr._sha256_bytes(manifest_bytes) != manifest_sha256:
        raise EvidenceError(f"{split_name} manifest bytes differ from the reviewed protocol")
    actual_split, dataset_seed, images = family_ocr._validate_manifest(manifest, manifest_path)
    if actual_split != split_name:
        raise EvidenceError(f"{split_name} protocol identity points to a {actual_split} manifest")
    report_path = family_ocr._require_artifact_path(report_path, REPOSITORY_ROOT, f"{split_name} report")
    report, report_bytes = family_ocr._load_object(report_path, f"{split_name} report")
    expected_report_hash = family_ocr._require_sha256(report_sha256, f"{split_name} report SHA-256")
    if family_ocr._sha256_bytes(report_bytes) != expected_report_hash:
        raise EvidenceError(f"{split_name} report bytes differ from the bound SHA-256")
    cases = family_ocr._validate_report(
        report, manifest_sha256, images, report_path=report_path, manifest_path=manifest_path)
    if report.get("schema") != family_ocr.REPORT_SCHEMA_V2:
        raise EvidenceError("original-input scoring requires v2 panel runtime reports")
    if family_ocr._require_sha256(report.get("candidate_sha256"), "report candidate SHA-256") != candidate["sha256"]:
        raise EvidenceError("runtime report candidate identity differs from its candidate")
    if (
        family_ocr._require_sha256(report.get("native_sha256"), "report native SHA-256")
        != candidate["native_sha256"]
        or report.get("native_scope") != candidate["native_scope"]
    ):
        raise EvidenceError("runtime report native identity differs from its candidate")
    assemblies = db_geometry._runtime_assemblies(report.get("runtime_assemblies"), "report runtime assemblies")
    if executed_assemblies is not None and assemblies != executed_assemblies:
        raise EvidenceError("original-input report assemblies differ from authenticated execution files")
    if expected_model_input == "original":
        if (
            report.get("model_input") != "original"
            or family_ocr._require_sha256(
                report.get("model_input_protocol_sha256"), "report model-input protocol SHA-256")
            != protocol_sha256
        ):
            raise EvidenceError("original-input report does not bind the reviewed input mode and protocol")
    elif "model_input" in report or "model_input_protocol_sha256" in report:
        raise EvidenceError("historical axis-masked baseline unexpectedly declares experimental input mode")
    return images, cases, report, report_bytes, dataset_seed, assemblies


def _metrics(
    images: Sequence[Mapping[str, Any]],
    truths: Mapping[str, Sequence[Any]],
    predictions: Mapping[str, Sequence[Component]],
) -> dict[str, Any]:
    metric_cases: dict[str, Mapping[str, Any]] = {}
    for image in images:
        source_sha = str(image["image_sha256"])
        metric_cases[source_sha] = {"_db_geometry_truths": tuple(truths[source_sha])}
    return db_geometry._metrics(images, metric_cases, predictions)


def _prevalidate_split_panels(
    images: Sequence[Mapping[str, Any]],
    baseline_cases: Mapping[str, Mapping[str, Any]],
    original_cases: Mapping[str, Mapping[str, Any]],
    baseline_candidate: Mapping[str, Any],
    original_candidate: Mapping[str, Any],
) -> tuple[dict[str, dict[str, dict[str, list[Component]]]], dict[str, Any]]:
    predictions = {
        "baseline_axis_masked": {stage: {} for stage in STAGES},
        "original_model_input": {stage: {} for stage in STAGES},
    }
    failures = {
        "baseline_axis_masked": {"failed_sources": 0, "failed_panels": 0},
        "original_model_input": {"failed_sources": 0, "failed_panels": 0},
    }
    compared_panels = changed_input_panels = 0
    panel_counts = {"baseline_axis_masked": 0, "original_model_input": 0}
    for image in images:
        source_sha = str(image["image_sha256"])
        baseline_source = baseline_cases[source_sha]
        original_source = original_cases[source_sha]
        for candidate_name in predictions:
            for stage in STAGES:
                predictions[candidate_name][stage][source_sha] = []
        source_records = (
            ("baseline_axis_masked", baseline_source, baseline_candidate, "axis-masked"),
            ("original_model_input", original_source, original_candidate, "original"),
        )
        for candidate_name, source, _, _ in source_records:
            failures[candidate_name]["failed_sources"] += int(source.get("status") == "failed")
            if source.get("status") != "failed":
                panel_counts[candidate_name] += len(_require_list(source.get("panels"), "source panels"))

        if baseline_source.get("status") == "failed" or original_source.get("status") == "failed":
            for candidate_name, source, candidate, mode in source_records:
                if source.get("status") == "failed":
                    continue
                for panel in _panel_map(source, f"{mode} source").values():
                    if panel.get("status") != "seed-completed":
                        failures[candidate_name]["failed_panels"] += 1
                        continue
                    stages, _, _, _ = _validate_completed_panel(panel, candidate, mode)
                    for stage in STAGES:
                        predictions[candidate_name][stage][source_sha].extend(stages[stage])
            continue

        baseline_panels = _panel_map(baseline_source, "baseline source")
        original_panels = _panel_map(original_source, "original source")
        if set(baseline_panels) != set(original_panels):
            raise EvidenceError("baseline and original-input reports use different panel crops")
        for crop in baseline_panels:
            baseline_panel = baseline_panels[crop]
            original_panel = original_panels[crop]
            baseline_stages, original_stages, changed_input = _validate_panel_pair(
                baseline_panel, original_panel, baseline_candidate, original_candidate)
            baseline_completed = baseline_panel.get("status") == "seed-completed"
            original_completed = original_panel.get("status") == "seed-completed"
            failures["baseline_axis_masked"]["failed_panels"] += int(not baseline_completed)
            failures["original_model_input"]["failed_panels"] += int(not original_completed)
            compared_panels += int(baseline_completed and original_completed)
            changed_input_panels += int(changed_input)
            for stage in STAGES:
                predictions["baseline_axis_masked"][stage][source_sha].extend(baseline_stages[stage])
                predictions["original_model_input"][stage][source_sha].extend(original_stages[stage])
    return predictions, {
        "failures": failures,
        "panel_counts": panel_counts,
        "compared_panels": compared_panels,
        "changed_input_panels": changed_input_panels,
    }


def score(
    protocol_path: Path,
    candidate_path: Path,
    candidate_sha256: str,
    execution_manifest_path: Path,
    execution_manifest_sha256: str,
    train_report_path: Path,
    train_report_sha256: str,
    dev_report_path: Path,
    dev_report_sha256: str,
    output_path: Path,
) -> dict[str, Any]:
    started = time.perf_counter()
    output_path = family_ocr._require_artifact_path(
        output_path, REPOSITORY_ROOT, "original-input score output")
    if output_path.exists():
        raise EvidenceError("refusing to replace an existing original-input score output")
    protocol, protocol_bytes, bound = _load_protocol(protocol_path)
    protocol_sha = family_ocr._sha256_bytes(protocol_bytes)
    baseline_candidate_path, baseline_candidate_sha = bound["baseline_candidate"]
    baseline_raw, baseline = _validate_complete_candidate(
        baseline_candidate_path, baseline_candidate_sha)
    original_raw, original = _validate_complete_candidate(
        candidate_path, family_ocr._require_sha256(candidate_sha256, "candidate SHA-256"))
    _validate_isolated_candidate(baseline_raw, original_raw, protocol_sha)
    execution_bytes, executed_assemblies = db_geometry._validate_execution_manifest(
        execution_manifest_path, execution_manifest_sha256)

    requested = {
        "train": (train_report_path, train_report_sha256),
        "validation": (dev_report_path, dev_report_sha256),
    }
    manifest_key = {"train": "train_manifest", "validation": "dev_manifest"}
    baseline_report_key = {
        "train": "baseline_train_report", "validation": "baseline_dev_report",
    }
    validated: dict[str, Any] = {}
    all_source_hashes: set[str] = set()
    for split_name in ("train", "validation"):
        manifest_path, manifest_sha = bound[manifest_key[split_name]]
        baseline_report_path, baseline_report_sha = bound[baseline_report_key[split_name]]
        baseline_run = _validate_report(
            split_name, manifest_path, manifest_sha, baseline_report_path,
            baseline_report_sha, baseline, "axis-masked", protocol_sha, None)
        original_run = _validate_report(
            split_name, manifest_path, manifest_sha, requested[split_name][0],
            requested[split_name][1], original, "original", protocol_sha, executed_assemblies)
        baseline_images, baseline_cases = baseline_run[0], baseline_run[1]
        original_images, original_cases = original_run[0], original_run[1]
        if baseline_images != original_images or set(baseline_cases) != set(original_cases):
            raise EvidenceError("baseline and original-input reports do not cover the same sources")
        current_hashes = set(baseline_cases)
        if all_source_hashes.intersection(current_hashes):
            raise EvidenceError("train and development reports reuse a source identity")
        all_source_hashes.update(current_hashes)
        prevalidated = _prevalidate_split_panels(
            baseline_images, baseline_cases, original_cases, baseline, original)
        validated[split_name] = (
            manifest_path, manifest_sha, baseline_report_path, baseline_report_sha,
            baseline_run, original_run, prevalidated,
        )

    metrics: dict[str, dict[str, dict[str, Any]]] = {
        "baseline_axis_masked": {stage: {} for stage in STAGES},
        "original_model_input": {stage: {} for stage in STAGES},
    }
    combined_images: list[dict[str, Any]] = []
    combined_truths: dict[str, Sequence[Any]] = {}
    combined_predictions = {
        candidate_name: {stage: {} for stage in STAGES}
        for candidate_name in metrics
    }
    runs: dict[str, Any] = {}
    total_truth = 0
    compared_panels = changed_input_panels = 0
    total_failures = {
        "baseline_axis_masked": {"failed_sources": 0, "failed_panels": 0},
        "original_model_input": {"failed_sources": 0, "failed_panels": 0},
    }
    baseline_assemblies: dict[str, tuple[tuple[str, str], ...]] = {}

    # Regeneration occurs only after all candidate, executable, manifest,
    # report, source PNG, crop PNG, and cross-split identities are validated.
    for split_name in ("train", "validation"):
        (
            manifest_path, manifest_sha, baseline_report_path, baseline_report_sha,
            baseline_run, original_run, prevalidated,
        ) = validated[split_name]
        images, baseline_cases, _, baseline_bytes, dataset_seed, old_assemblies = baseline_run
        _, _, _, original_bytes, _, _ = original_run
        predictions, validation_stats = prevalidated
        baseline_assemblies[split_name] = old_assemblies
        regenerated = family_ocr._regenerate(split_name, dataset_seed, images)
        truths: dict[str, Sequence[Any]] = {}
        truth_count = 0
        for image in images:
            source_sha = str(image["image_sha256"])
            _, annotation = regenerated[source_sha]
            source_truths, _ = family_ocr._truth_regions(annotation)
            truths[source_sha] = source_truths
            truth_count += len(source_truths)
        expected_truth = EXPECTED_TRUTH_COUNTS[split_name]
        if truth_count != expected_truth:
            raise EvidenceError(
                f"{split_name} regenerated truth count {truth_count} differs from fixed denominator {expected_truth}")
        total_truth += truth_count
        compared_panels += validation_stats["compared_panels"]
        changed_input_panels += validation_stats["changed_input_panels"]
        for candidate_name in total_failures:
            for field in total_failures[candidate_name]:
                total_failures[candidate_name][field] += validation_stats["failures"][candidate_name][field]
        for candidate_name in metrics:
            for stage in STAGES:
                metrics[candidate_name][stage][split_name] = _metrics(
                    images, truths, predictions[candidate_name][stage])
                combined_predictions[candidate_name][stage].update(predictions[candidate_name][stage])
        combined_images.extend(images)
        combined_truths.update(truths)
        runs[split_name] = {
            "manifest": {
                "path": manifest_path.relative_to(REPOSITORY_ROOT).as_posix(),
                "sha256": manifest_sha,
            },
            "baseline_report": {
                "path": baseline_report_path.relative_to(REPOSITORY_ROOT).as_posix(),
                "sha256": baseline_report_sha,
            },
            "original_input_report": {
                "path": requested[split_name][0].resolve().relative_to(REPOSITORY_ROOT.resolve()).as_posix(),
                "sha256": family_ocr._sha256_bytes(original_bytes),
            },
            "source_count": len(images),
            "panel_counts": validation_stats["panel_counts"],
            "truth_region_count": truth_count,
            "failures_by_candidate": validation_stats["failures"],
            "baseline_report_sha256_verified": family_ocr._sha256_bytes(baseline_bytes) == baseline_report_sha,
        }
    if total_truth != EXPECTED_TOTAL_TRUTHS:
        raise EvidenceError(
            f"combined truth count {total_truth} differs from fixed denominator {EXPECTED_TOTAL_TRUTHS}")
    for candidate_name in metrics:
        for stage in STAGES:
            metrics[candidate_name][stage]["combined"] = _metrics(
                combined_images, combined_truths, combined_predictions[candidate_name][stage])

    result = {
        "schema": OUTPUT_SCHEMA,
        "status": "diagnostic_only_unapproved",
        "scope": "local-synthetic-train-dev-original-model-input-diagnostic",
        "model_input": "original",
        "model_input_protocol_sha256": protocol_sha,
        "synthetic_only": True,
        "private_data": False,
        "sealed_data": False,
        "sealed_runs": 0,
        "optimizer_steps": 0,
        "production_approval": False,
        "release_eligible": False,
        "protocol": {
            "path": PROTOCOL_PATH.as_posix(),
            "sha256": protocol_sha,
            "hypothesis": protocol["hypothesis"],
            "isolated_change": protocol["isolated_change"],
            "acceptance_bar_reference": protocol["acceptance_bar"],
        },
        "candidates": {
            "baseline_axis_masked": {
                "path": baseline_candidate_path.relative_to(REPOSITORY_ROOT).as_posix(),
                **baseline,
            },
            "original_model_input": {
                "path": candidate_path.resolve().relative_to(REPOSITORY_ROOT.resolve()).as_posix(),
                **original,
            },
        },
        "execution_manifest": {
            "path": execution_manifest_path.resolve().relative_to(REPOSITORY_ROOT.resolve()).as_posix(),
            "sha256": family_ocr._sha256_bytes(execution_bytes),
            "all_declared_files_verified": True,
            "runtime_assemblies": [
                {"name": name, "sha256": file_sha} for name, file_sha in executed_assemblies
            ],
        },
        "runs": runs,
        "integrity": {
            "protocol_and_all_bound_files_verified": True,
            "candidate_is_exact_baseline_clone_plus_input_mode": True,
            "native_model_manifest_license_and_execution_files_verified": True,
            "source_and_crop_png_bytes_verified": True,
            "original_and_axis_masked_input_hashes_bound": True,
            "raw_learned_and_structural_diagnostics_unchanged_between_runs": True,
            "final_regions_are_consensus_selected_raw_geometry": True,
            "truth_created_only_after_all_runtime_evidence_validation": True,
            "full_source_truth_count": total_truth,
            "compared_panel_count": compared_panels,
            "panels_with_distinct_original_and_axis_masked_inputs": changed_input_panels,
            "failures_by_candidate": total_failures,
            "baseline_runtime_assemblies_by_split": {
                split: [{"name": name, "sha256": file_sha} for name, file_sha in assemblies]
                for split, assemblies in baseline_assemblies.items()
            },
        },
        "evaluator": {
            "matching": "existing maximum-cardinality one-to-one matching",
            "intersection_over_union_minimum": family_ocr.MATCH_IOU_MINIMUM,
            "geometry": "default expanded learned-detector polygons in original source pixels",
            "initial_detector_boxes_combined": False,
            "model_input_resolution_changed": False,
            "recognition_scored": False,
            "roles_scored": False,
        },
        "detection_metrics": metrics,
        "limitations": [
            "Consensus geometry is scored from final persisted OCR regions after verifying zero region failures, crop count equality, and exact raw-region identity and polygon equality.",
            "Failed source or panel executions contribute no predictions while all regenerated source truths remain in the denominator.",
            "Recognition text, alternatives, semantic roles, confidence, and threshold sensitivity are not scored.",
            "This synthetic train/dev diagnostic cannot approve a model, product stage, production activation, or release.",
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
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--candidate-sha256", required=True)
    parser.add_argument("--execution-manifest", type=Path, required=True)
    parser.add_argument("--execution-manifest-sha256", required=True)
    parser.add_argument("--train-report", type=Path, required=True)
    parser.add_argument("--train-report-sha256", required=True)
    parser.add_argument("--dev-report", type=Path, required=True)
    parser.add_argument("--dev-report-sha256", required=True)
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    try:
        result = score(
            arguments.protocol,
            arguments.candidate,
            arguments.candidate_sha256,
            arguments.execution_manifest,
            arguments.execution_manifest_sha256,
            arguments.train_report,
            arguments.train_report_sha256,
            arguments.dev_report,
            arguments.dev_report_sha256,
            arguments.output,
        )
    except (EvidenceError, OSError, KeyError, TypeError, json.JSONDecodeError) as exception:
        print(json.dumps({"status": "rejected", "error": str(exception)}, sort_keys=True), file=sys.stderr)
        return 1
    print(json.dumps({
        "status": result["status"],
        "model_input": result["model_input"],
        "full_source_truth_count": result["integrity"]["full_source_truth_count"],
        "output": str(arguments.output),
        "production_approval": False,
    }, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
