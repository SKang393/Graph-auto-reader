# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Score retained unmasked DB regions on the authenticated V3 exchange.

The C# reports are annotation-free.  This evaluator first authenticates the
complete V3 binding, manifests, reports, source PNGs, panel bytes, transforms,
and runtime implementation.  Only then does it regenerate project-owned text
truth and apply the established fixed-IoU maximum-cardinality matcher.
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

import score_db_geometry_observation as db_geometry
import score_family_ocr as family_ocr
from ml.markers.center.plot_domain_v25 import runtime_domain_binding_v3
from ml.ocr.component_region_detector_v6.dataset import Box, Component
from ml.ocr.real_range_proposal_v34.pipeline import maximum_cardinality_matches


OUTPUT_SCHEMA = "graphreader.synthetic-v3-original-unmasked-ocr-score.v1"
MATCH_IOU_MINIMUM = 0.5
EXPECTED_TRUTH_COUNTS = {"train": 709, "validation": 183}
EXPECTED_SOURCE_COUNTS = {"train": 20, "validation": 3}
EXPECTED_PANEL_COUNTS = {"train": 28, "validation": 9}
EXPECTED_TOTAL_TRUTHS = 892

EvidenceError = family_ocr.EvidenceError


def _sha(payload: bytes) -> str:
    return sha256(payload).hexdigest()


def _read_bound_json(path: Path, expected_sha256: str, label: str) -> tuple[dict[str, Any], bytes]:
    payload = path.read_bytes()
    if _sha(payload) != family_ocr._require_sha256(expected_sha256, f"{label} SHA-256"):
        raise EvidenceError(f"{label} bytes differ from the binding")
    try:
        value = json.loads(payload)
    except json.JSONDecodeError as exception:
        raise EvidenceError(f"{label} is invalid JSON: {exception}") from exception
    if not isinstance(value, dict):
        raise EvidenceError(f"{label} must be a JSON object")
    return value, payload


def _resolve_bound_path(root: Path, path: Path, label: str) -> Path:
    resolved = (root / path).resolve() if not path.is_absolute() else path.resolve()
    artifact_root = (root / "artifacts").resolve()
    if artifact_root not in resolved.parents:
        raise EvidenceError(f"{label} must remain below the repository artifacts directory")
    return resolved


def _preauthenticate_exchange_files(
    binding_document: Mapping[str, Any], root: Path
) -> None:
    train = binding_document.get("train")
    dev = binding_document.get("dev")
    if not isinstance(train, list) or not train or not isinstance(dev, dict):
        raise EvidenceError("V3 binding does not declare complete train and development exchanges")
    records = [(f"train exchange {index}", record) for index, record in enumerate(train)]
    records.append(("development exchange", dev))
    for label, raw in records:
        if not isinstance(raw, dict):
            raise EvidenceError(f"{label} must be an object")
        for kind in ("manifest", "report"):
            path_value = raw.get(f"{kind}_path")
            hash_value = raw.get(f"{kind}_sha256")
            if not isinstance(path_value, str):
                raise EvidenceError(f"{label} {kind} path must be a string")
            path = _resolve_bound_path(root, Path(path_value), f"{label} {kind}")
            _read_bound_json(path, hash_value, f"{label} {kind}")


def _transform_point(matrix: Sequence[float], x: float, y: float) -> tuple[float, float]:
    if len(matrix) != 9 or not all(math.isfinite(float(value)) for value in matrix):
        raise EvidenceError("panel-to-source matrix must contain nine finite values")
    denominator = float(matrix[6]) * x + float(matrix[7]) * y + float(matrix[8])
    if not math.isfinite(denominator) or abs(denominator) < 1e-12:
        raise EvidenceError("panel-to-source matrix has an invalid homogeneous denominator")
    source_x = (float(matrix[0]) * x + float(matrix[1]) * y + float(matrix[2])) / denominator
    source_y = (float(matrix[3]) * x + float(matrix[4]) * y + float(matrix[5])) / denominator
    if not math.isfinite(source_x) or not math.isfinite(source_y):
        raise EvidenceError("panel-to-source mapping produced a nonfinite coordinate")
    return source_x, source_y


def _component_from_points(
    points: Sequence[tuple[float, float]], matrix: Sequence[float]
) -> Component:
    mapped = tuple(_transform_point(matrix, x, y) for x, y in points)
    left = min(point[0] for point in mapped)
    top = min(point[1] for point in mapped)
    right = max(point[0] for point in mapped)
    bottom = max(point[1] for point in mapped)
    if right <= left or bottom <= top:
        raise EvidenceError("source-mapped unmasked OCR region has nonpositive bounds")
    return Component(left, top, right - 1.0, bottom - 1.0, (right - left) * (bottom - top), 1)


def _regions_from_panel(panel: Mapping[str, Any]) -> tuple[Component, ...]:
    if panel.get("status") != "seed-completed":
        return ()
    width = family_ocr._require_int(panel.get("width"), "panel width", minimum=1)
    height = family_ocr._require_int(panel.get("height"), "panel height", minimum=1)
    diagnostic = db_geometry._require_mapping(
        panel.get("ocr_proposal_diagnostic"), "OCR proposal diagnostic")
    if (
        diagnostic.get("coordinate_space") != "original_pixels"
        or diagnostic.get("used_as_accepted_evidence") is not False
    ):
        raise EvidenceError("unmasked OCR diagnostic coordinate space or evidence use is invalid")
    source_gray = db_geometry._require_mapping(panel.get("source_gray"), "panel source Gray8")
    gray_sha = family_ocr._require_sha256(source_gray.get("sha256"), "panel source Gray8 SHA-256")
    if family_ocr._require_sha256(
        diagnostic.get("unmasked_input_sha256"), "unmasked OCR input SHA-256"
    ) != gray_sha:
        raise EvidenceError("unmasked OCR regions do not bind the retained original Gray8 input")
    matrix = db_geometry._matrix_tuple(
        panel.get("panel_to_source_matrix"), "panel-to-source matrix")
    raw_regions = db_geometry._require_list(
        diagnostic.get("unmasked_model_regions"), "unmasked OCR regions")
    identifiers: set[str] = set()
    predictions: list[Component] = []
    for index, raw_region in enumerate(raw_regions):
        region = db_geometry._require_mapping(raw_region, f"unmasked OCR region {index}")
        identifier = region.get("region_id")
        if not isinstance(identifier, str) or not identifier or identifier in identifiers:
            raise EvidenceError("unmasked OCR region identities must be present and panel-unique")
        identifiers.add(identifier)
        if region.get("coordinate_space") != "original_pixels":
            raise EvidenceError("unmasked OCR region is not in panel original-pixel coordinates")
        points = db_geometry._polygon_points(
            region.get("polygon"), f"unmasked OCR region {index} polygon", width, height)
        predictions.append(_component_from_points(points, matrix))
    return tuple(predictions)


def _metrics(
    source_hashes: Sequence[str],
    truths: Mapping[str, Sequence[Box]],
    predictions: Mapping[str, Sequence[Component]],
) -> dict[str, Any]:
    truth_count = prediction_count = matches = 0
    for source_hash in source_hashes:
        current_truths = tuple(truths[source_hash])
        current_predictions = tuple(predictions.get(source_hash, ()))
        truth_count += len(current_truths)
        prediction_count += len(current_predictions)
        matches += maximum_cardinality_matches(current_predictions, current_truths)
    return {
        "source_count": len(source_hashes),
        "truth_region_count": truth_count,
        "predicted_region_count": prediction_count,
        "true_positives": matches,
        "false_positives": prediction_count - matches,
        "false_negatives": truth_count - matches,
        "precision": matches / max(1, prediction_count),
        "recall": matches / max(1, truth_count),
    }


def _truth_fully_covered(truth: Box, crops: Sequence[Mapping[str, Any]]) -> bool:
    return any(
        truth.left >= float(crop["x"])
        and truth.top >= float(crop["y"])
        and truth.right <= float(crop["x"]) + float(crop["width"])
        and truth.bottom <= float(crop["y"]) + float(crop["height"])
        for crop in crops
    )


def score(
    binding_path: Path,
    binding_sha256: str,
    output_path: Path,
    *,
    repository_root: Path = REPOSITORY_ROOT,
) -> dict[str, Any]:
    started = time.perf_counter()
    root = repository_root.resolve()
    binding_path = _resolve_bound_path(root, binding_path, "V3 binding")
    output_path = _resolve_bound_path(root, output_path, "score output")
    if output_path.exists():
        raise EvidenceError("refusing to replace an existing V3 OCR score")
    binding_bytes = binding_path.read_bytes()
    expected_binding_sha = family_ocr._require_sha256(binding_sha256, "V3 binding SHA-256")
    if _sha(binding_bytes) != expected_binding_sha:
        raise EvidenceError("V3 binding bytes differ from the caller-bound SHA-256")
    try:
        binding_document = json.loads(binding_bytes)
    except json.JSONDecodeError as exception:
        raise EvidenceError(f"V3 binding is invalid JSON: {exception}") from exception
    if not isinstance(binding_document, dict):
        raise EvidenceError("V3 binding must be a JSON object")
    # The shared loader deliberately regenerates each split during validation.
    # Authenticate every exchange first so no annotation object exists before
    # the complete annotation-free train/development corpus is byte-bound.
    _preauthenticate_exchange_files(binding_document, root)

    try:
        domains = runtime_domain_binding_v3.load_runtime_domain_binding_v3(
            binding_path, expected_binding_sha, repository_root=root)
    except runtime_domain_binding_v3.RuntimeDomainBindingError as exception:
        raise EvidenceError(str(exception)) from exception
    if domains.failures:
        raise EvidenceError("V3 domain binding contains unresolved runtime failures")

    split_records = {
        "train": (domains.train_bindings, domains.train, domains.profile.train),
        "validation": ((domains.dev_binding,), domains.dev, domains.profile.dev),
    }
    all_truths: dict[str, tuple[Box, ...]] = {}
    all_predictions: dict[str, list[Component]] = {}
    all_sources: list[str] = []
    runs: dict[str, list[dict[str, Any]]] = {"train": [], "validation": []}
    split_source_hashes: dict[str, list[str]] = {"train": [], "validation": []}
    split_panel_counts = {"train": 0, "validation": 0}
    split_outside_crop = {"train": 0, "validation": 0}

    # The loader has authenticated every annotation-free byte and implementation
    # identity. Regenerated truth is not read until all bound reports below have
    # also been re-read and matched to their frozen hashes.
    validated_reports: list[tuple[str, Any, dict[str, Any], dict[str, Any], bytes]] = []
    for split, (bindings, panel_domains, profile) in split_records.items():
        expected_panels = {
            (item.runtime_input.source_sha256, item.runtime_input.panel_id): item
            for item in panel_domains
        }
        observed_panels: set[tuple[str, str]] = set()
        for evidence_binding in bindings:
            manifest_path = _resolve_bound_path(root, evidence_binding.manifest_path, f"{split} manifest")
            report_path = _resolve_bound_path(root, evidence_binding.report_path, f"{split} report")
            manifest, manifest_bytes = _read_bound_json(
                manifest_path, evidence_binding.manifest_sha256, f"{split} manifest")
            report, report_bytes = _read_bound_json(
                report_path, evidence_binding.report_sha256, f"{split} report")
            images = manifest.get("images")
            if not isinstance(images, list):
                raise EvidenceError(f"{split} manifest images must be an array")
            image_by_sha = {str(item["image_sha256"]): item for item in images}
            cases = report.get("cases")
            if not isinstance(cases, list):
                raise EvidenceError(f"{split} report cases must be an array")
            for case in cases:
                source_sha = str(case.get("image_sha256"))
                if source_sha not in image_by_sha:
                    raise EvidenceError(f"{split} report contains a foreign source")
                source_panels = case.get("panels")
                if not isinstance(source_panels, list):
                    raise EvidenceError(f"{split} report source panels must be an array")
                all_predictions.setdefault(source_sha, [])
                for panel in source_panels:
                    key = (source_sha, str(panel.get("panel_id")))
                    if key not in expected_panels or key in observed_panels:
                        raise EvidenceError(f"{split} report panel inventory differs from the V3 binding")
                    observed_panels.add(key)
                    bound_panel = expected_panels[key]
                    if tuple(panel.get("panel_to_source_matrix", ())) != bound_panel.panel_to_source_matrix:
                        raise EvidenceError("report panel-to-source matrix differs from the V3 binding")
                    all_predictions[source_sha].extend(_regions_from_panel(panel))
                    split_panel_counts[split] += 1
            if set(image_by_sha).intersection(all_sources):
                raise EvidenceError("V3 reports repeat a source identity")
            all_sources.extend(image_by_sha)
            split_source_hashes[split].extend(image_by_sha)
            validated_reports.append((split, evidence_binding, manifest, report, report_bytes))
            runs[split].append({
                "dataset_seed": manifest.get("seed"),
                "manifest": {
                    "path": manifest_path.relative_to(root).as_posix(),
                    "sha256": _sha(manifest_bytes),
                },
                "report": {
                    "path": report_path.relative_to(root).as_posix(),
                    "sha256": _sha(report_bytes),
                },
                "source_count": len(images),
                "panel_count": sum(len(case["panels"]) for case in cases),
            })
        if observed_panels != set(expected_panels):
            raise EvidenceError(f"{split} report panel inventory is incomplete")

    for split, evidence_binding, manifest, report, _ in validated_reports:
        images = manifest["images"]
        try:
            regenerated = runtime_domain_binding_v3._regenerate_v3_records(
                split, int(manifest["seed"]), images, domains.profile,
            )
        except runtime_domain_binding_v3.RuntimeDomainBindingError as exception:
            raise EvidenceError(str(exception)) from exception
        case_by_sha = {str(case["image_sha256"]): case for case in report["cases"]}
        for image in images:
            source_sha = str(image["image_sha256"])
            truths, _ = family_ocr._truth_regions(regenerated[source_sha].annotation)
            if source_sha in all_truths:
                raise EvidenceError("regenerated truth repeats a source identity")
            all_truths[source_sha] = truths
            crops = [panel["crop"] for panel in case_by_sha[source_sha]["panels"]]
            split_outside_crop[split] += sum(
                not _truth_fully_covered(truth, crops) for truth in truths)

    metrics: dict[str, dict[str, Any]] = {}
    for split in ("train", "validation"):
        if len(split_source_hashes[split]) != EXPECTED_SOURCE_COUNTS[split]:
            raise EvidenceError(f"{split} source count differs from the fixed V3 denominator")
        if split_panel_counts[split] != EXPECTED_PANEL_COUNTS[split]:
            raise EvidenceError(f"{split} panel count differs from the fixed V3 exchange")
        metrics[split] = _metrics(split_source_hashes[split], all_truths, all_predictions)
        if metrics[split]["truth_region_count"] != EXPECTED_TRUTH_COUNTS[split]:
            raise EvidenceError(f"{split} truth count differs from the fixed full-source denominator")
        metrics[split]["panel_count"] = split_panel_counts[split]
        metrics[split]["truth_regions_outside_all_emitted_crops"] = split_outside_crop[split]
    metrics["combined"] = _metrics(all_sources, all_truths, all_predictions)
    if metrics["combined"]["truth_region_count"] != EXPECTED_TOTAL_TRUTHS:
        raise EvidenceError("combined truth count differs from the fixed full-source denominator")

    result = {
        "schema": OUTPUT_SCHEMA,
        "status": "diagnostic_only_unapproved",
        "scope": "local-project-owned-synthetic-v3-original-unmasked-ocr",
        "synthetic_only": True,
        "private_data": False,
        "sealed_data": False,
        "sealed_runs": 0,
        "optimizer_steps": 0,
        "production_approval": False,
        "release_eligible": False,
        "binding": {
            "path": binding_path.relative_to(root).as_posix(),
            "sha256": expected_binding_sha,
            "all_reports_sources_panels_transforms_and_runtime_inputs_verified": True,
        },
        "generator": {
            "version": domains.profile.generator_version,
            "protocol": {
                "path": domains.profile.protocol_path.as_posix(),
                "sha256": domains.profile.protocol_sha256,
            },
            "sources": [
                {"path": item.relative_path.as_posix(), "sha256": item.sha256}
                for item in domains.profile.sources
            ],
            "train_scene_identity_set_sha256": domains.profile.train.resolved_scene_identity_set_sha256,
            "validation_scene_identity_set_sha256": domains.profile.dev.resolved_scene_identity_set_sha256,
            "all_source_pngs_regenerated_byte_identical_before_truth_use": True,
        },
        "runtime": {
            "candidate_path": domains.implementation.candidate_path.as_posix(),
            "candidate_sha256": domains.implementation.candidate_sha256,
            "models": [vars(item) for item in domains.implementation.models],
            "runs": runs,
        },
        "evaluator": {
            "matching": "existing maximum-cardinality one-to-one IoU matching",
            "intersection_over_union_minimum": MATCH_IOU_MINIMUM,
            "prediction_source": "retained ocr_proposal_diagnostic.unmasked_model_regions",
            "model_input": "original Gray8 panel converted by the recorded DB detector route",
            "coordinate_mapping": "authenticated panel_to_source_matrix applied once",
            "source_sha256": _sha(Path(__file__).read_bytes()),
            "matcher_source": {
                "path": "ml/ocr/real_range_proposal_v34/pipeline.py",
                "sha256": _sha((root / "ml/ocr/real_range_proposal_v34/pipeline.py").read_bytes()),
            },
        },
        "integrity": {
            "truth_created_only_after_all_annotation_free_reports_authenticated": True,
            "full_source_truth_denominator": EXPECTED_TOTAL_TRUTHS,
            "no_truth_used_by_runtime": True,
            "failed_or_omitted_truths_never_removed": True,
        },
        "metrics": metrics,
        "limitations": [
            "This is a saved-output train/development diagnostic and cannot approve an OCR model or product stage.",
            "Only original-input unmasked DB region detection is scored; recognition, role assignment, calibration, and export are not scored.",
            "Predictions from overlapping panels are retained independently, so duplicate detections remain false positives under one-to-one source-truth matching.",
            "The fixed detector output and IoU 0.5 operating point are measured without threshold or candidate selection.",
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
    parser.add_argument("--binding", type=Path, required=True)
    parser.add_argument("--binding-sha256", required=True)
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    result = score(arguments.binding, arguments.binding_sha256, arguments.output)
    print(json.dumps({
        "status": result["status"],
        "train": result["metrics"]["train"],
        "validation": result["metrics"]["validation"],
        "output_sha256": _sha(arguments.output.read_bytes()),
    }, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
