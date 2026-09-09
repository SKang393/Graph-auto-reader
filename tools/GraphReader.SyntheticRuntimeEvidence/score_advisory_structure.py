# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Score the authenticated advisory structure-admission OCR experiment."""

from __future__ import annotations

import argparse
from copy import deepcopy
from hashlib import sha256
import json
import math
from pathlib import Path
import statistics
import struct
import sys
import time
from typing import Any, Mapping, Sequence
from uuid import UUID


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
if str(REPOSITORY_ROOT) not in sys.path:
    sys.path.insert(0, str(REPOSITORY_ROOT))

import score_initial_contour_output as initial_scorer  # noqa: E402


family_ocr = initial_scorer.family_ocr
db_geometry = initial_scorer.db_geometry
original_scorer = initial_scorer.original_scorer
EvidenceError = family_ocr.EvidenceError

PROTOCOL_PATH = Path("ml/ocr/official_bakeoff/advisory_structure_dev_protocol.json")
EXPECTED_PROTOCOL_SHA256 = "3210a4312b83531d1192f50eafe47c54407e10cd3411558a5c45dd7c709846eb"
BASELINE_CANDIDATE_PATH = Path(
    "artifacts/synthetic-runtime-evidence/initial-contour-source-c96/candidate.json")
EXPECTED_BASELINE_CANDIDATE_SHA256 = (
    "15dc39a373942278bec23f029cb59f1f13d63b845204f1f238ae2a52e2119958")
BASELINE_EXECUTION_PATH = Path(
    "artifacts/synthetic-runtime-evidence/initial-contour-source-c96-executable-v1/execution-files.json")
EXPECTED_BASELINE_EXECUTION_SHA256 = (
    "cb4c6b50d1e64213c20b8389cebe6ed6493e72c01f2fe0aeaf010f0b925b7f0b")
BASELINE_REPORTS = {
    "train": (
        Path("artifacts/synthetic-runtime-evidence/train393-initial-contour-source-c96-run1/report.json"),
        "00b2910903d1bace5cfef06ab9e94a7275debe54eb1e897bf75b32ee64dd09f1",
    ),
    "validation": (
        Path("artifacts/synthetic-runtime-evidence/dev393-initial-contour-source-c96-run1/report.json"),
        "b63b3abc31b2ef9bc9c085f105f912ff2e914aa4437e07a95ca62b8c33cf004a",
    ),
}
INITIAL_SCORER_PATH = Path(
    "tools/GraphReader.SyntheticRuntimeEvidence/score_initial_contour_output.py")
EXPECTED_INITIAL_SCORER_SHA256 = (
    "b8c705659c7816d333ca03618461bba82f2c0fb45098e682d86360aabff48502")
DIRECT_HELPER_BINDINGS = {
    Path("tools/GraphReader.SyntheticRuntimeEvidence/score_original_model_input.py"):
        "a36245ec82dcba0bc4f1a29ae04fc4c1417bf2d9ad113ff5b30d9ac93385b756",
    Path("tools/GraphReader.SyntheticRuntimeEvidence/score_db_geometry_observation.py"):
        "12569c6e68581d57959cf226eb91c8d1cc429ea5cf9cc111727bc9e52e9bea10",
    Path("tools/GraphReader.SyntheticRuntimeEvidence/score_family_ocr.py"):
        "431392d074e985f953a1444982076466f461d3445c018a5c307f174ebc0a959e",
}
ADVISORY_COMPOSITION = (
    "graph-structure-consensus-advisory-initial-db-contour-original-model-input-v1")
OUTPUT_SCHEMA = "graphreader.synthetic-advisory-structure-score.v1"
PROTOCOL_KEYS = {
    "evidence_policy", "hypothesis", "isolated_change", "split_identities",
    "metric", "acceptance_bar", "budget",
}
IDENTITY_KEYS = {
    "baseline_candidate_path", "baseline_candidate_sha256",
    "baseline_protocol_path", "baseline_protocol_sha256",
    "train_manifest_path", "train_manifest_sha256", "dev_manifest_path",
    "dev_manifest_sha256", "train_source_count", "train_panel_count",
    "train_truth_count", "dev_source_count", "dev_panel_count", "dev_truth_count",
}
EXPECTED_COUNTS = {
    "train": {"sources": 4, "panels": 4, "truths": 146},
    "validation": {"sources": 3, "panels": 9, "truths": 183},
}
EXPECTED_TOTAL_TRUTHS = 329


def _repository_path(relative: Path, label: str) -> Path:
    if relative.is_absolute():
        raise EvidenceError(f"{label} must be repository-relative")
    root = REPOSITORY_ROOT.resolve()
    path = (root / relative).resolve()
    if path == root or root not in path.parents:
        raise EvidenceError(f"{label} escaped the repository")
    return path


def _exact_file(relative: Path, expected_sha256: str, label: str) -> tuple[Path, bytes]:
    path = _repository_path(relative, label)
    if not path.is_file():
        raise EvidenceError(f"{label} is missing")
    payload = path.read_bytes()
    expected = _local_sha256(expected_sha256, f"{label} SHA-256")
    if sha256(payload).hexdigest() != expected:
        raise EvidenceError(f"{label} bytes differ from the fixed identity")
    return path, payload


def _local_sha256(value: Any, label: str) -> str:
    if (
        not isinstance(value, str)
        or len(value) != 64
        or value != value.lower()
        or any(character not in "0123456789abcdef" for character in value)
    ):
        raise EvidenceError(f"{label} must be a lowercase SHA-256")
    return value


def _validate_evaluator_sources(evaluator_sha256: str) -> str:
    expected = _local_sha256(
        evaluator_sha256, "reviewed advisory evaluator SHA-256")
    if sha256(Path(__file__).read_bytes()).hexdigest() != expected:
        raise EvidenceError("current advisory evaluator bytes differ from the reviewed identity")
    _exact_file(INITIAL_SCORER_PATH, EXPECTED_INITIAL_SCORER_SHA256, "initial-contour scorer")
    for path, digest in DIRECT_HELPER_BINDINGS.items():
        _exact_file(path, digest, f"direct helper {path.as_posix()}")
    return expected


def _validate_new_output_path(path: Path) -> Path:
    root = REPOSITORY_ROOT.resolve()
    artifacts = (root / "artifacts").resolve()
    output = path.resolve()
    if output == artifacts or artifacts not in output.parents:
        raise EvidenceError("advisory score output must stay under repository artifacts")
    if output.exists():
        raise EvidenceError("advisory score output must use a new destination")
    return output


def _load_protocol(path: Path) -> tuple[dict[str, Any], str, dict[str, Any]]:
    expected_path = _repository_path(PROTOCOL_PATH, "advisory protocol")
    if path.resolve() != expected_path:
        raise EvidenceError("advisory scoring requires the reviewed repository protocol path")
    protocol, payload = family_ocr._load_object(expected_path, "advisory protocol")
    protocol_sha = family_ocr._sha256_bytes(payload)
    if protocol_sha != EXPECTED_PROTOCOL_SHA256:
        raise EvidenceError("advisory protocol bytes differ from the reviewed identity")
    family_ocr._require_exact_keys(protocol, PROTOCOL_KEYS, "advisory protocol")
    if (
        protocol.get("evidence_policy") != "ml/policy/evidence-policy.json"
        or not all(isinstance(protocol.get(field), str) and protocol[field].strip()
                   for field in ("hypothesis", "isolated_change", "metric", "acceptance_bar"))
    ):
        raise EvidenceError("advisory protocol metadata is invalid")
    budget = db_geometry._require_mapping(protocol.get("budget"), "advisory budget")
    family_ocr._require_exact_keys(
        budget,
        {"synthetic_train_dev_runs", "optimizer_steps", "private_reads", "sealed_runs",
         "production_approval", "release_eligible"},
        "advisory budget")
    if budget != {
        "synthetic_train_dev_runs": "unlimited", "optimizer_steps": 0,
        "private_reads": 0, "sealed_runs": 0, "production_approval": False,
        "release_eligible": False,
    }:
        raise EvidenceError("advisory protocol cannot authorize training, private, sealed, production, or release use")
    identities = dict(db_geometry._require_mapping(
        protocol.get("split_identities"), "advisory split identities"))
    family_ocr._require_exact_keys(identities, IDENTITY_KEYS, "advisory split identities")
    if (
        identities["baseline_candidate_path"] != BASELINE_CANDIDATE_PATH.as_posix()
        or identities["baseline_candidate_sha256"] != EXPECTED_BASELINE_CANDIDATE_SHA256
        or identities["baseline_protocol_path"] != initial_scorer.PROTOCOL_PATH.as_posix()
        or identities["baseline_protocol_sha256"] != initial_scorer.EXPECTED_PROTOCOL_SHA256
    ):
        raise EvidenceError("advisory protocol does not bind the frozen initial-contour baseline")
    for split, prefix in (("train", "train"), ("validation", "dev")):
        expected = EXPECTED_COUNTS[split]
        if any(identities[f"{prefix}_{name}_count"] != expected[name + "s"]
               for name in ("source", "panel", "truth")):
            raise EvidenceError(f"advisory {split} denominator differs from the reviewed protocol")
    return protocol, protocol_sha, identities


def _validate_isolated_candidate(
    baseline: Mapping[str, Any], candidate: Mapping[str, Any], protocol_sha: str,
) -> None:
    if candidate.get("ocr_structure_admission") != "advisory":
        raise EvidenceError("candidate does not select advisory structure admission")
    if candidate.get("advisory_structure_protocol") != {
        "path": PROTOCOL_PATH.as_posix(), "sha256": protocol_sha,
    }:
        raise EvidenceError("candidate does not bind the reviewed advisory protocol")
    normalized = deepcopy(dict(candidate))
    normalized.pop("ocr_structure_admission", None)
    normalized.pop("advisory_structure_protocol", None)
    if normalized != baseline:
        raise EvidenceError("candidate changes more than advisory admission and its protocol")


def _validate_baseline_ancestry(
    initial_candidate: Mapping[str, Any], initial_protocol_sha256: str,
) -> tuple[Mapping[str, Any], Mapping[str, Any], str]:
    _, original_protocol_bytes, original_bound = original_scorer._load_protocol(
        _repository_path(
            initial_scorer.ORIGINAL_PROTOCOL_PATH, "original-input protocol"))
    original_protocol_sha = family_ocr._sha256_bytes(original_protocol_bytes)
    if original_protocol_sha != initial_scorer.EXPECTED_ORIGINAL_PROTOCOL_SHA256:
        raise EvidenceError("original-input ancestry protocol identity changed")
    original_raw, original_candidate = original_scorer._validate_complete_candidate(
        _repository_path(
            initial_scorer.ORIGINAL_CANDIDATE_PATH, "original-input candidate"),
        initial_scorer.EXPECTED_ORIGINAL_CANDIDATE_SHA256)
    axis_raw, axis_candidate = original_scorer._validate_complete_candidate(
        *original_bound["baseline_candidate"])
    original_scorer._validate_isolated_candidate(
        axis_raw, original_raw, original_protocol_sha)
    initial_scorer._validate_isolated_candidate(
        original_raw, initial_candidate, initial_protocol_sha256)
    return axis_candidate, original_candidate, original_protocol_sha


def _write_7bit_length(value: int) -> bytes:
    if value < 0:
        raise ValueError("length must be nonnegative")
    result = bytearray()
    while value >= 0x80:
        result.append((value | 0x80) & 0xFF)
        value >>= 7
    result.append(value)
    return bytes(result)


def _advisory_region_id(model_region_id: str, polygon: Mapping[str, Any]) -> str:
    try:
        UUID(model_region_id)
    except ValueError as exception:
        raise EvidenceError("advisory source model region ID must be a UUID") from exception
    points = db_geometry._require_list(polygon.get("points"), "advisory initial polygon points")
    if len(points) < 3:
        raise EvidenceError("advisory initial polygon must have at least three points")
    material = bytearray()
    for value in (ADVISORY_COMPOSITION, model_region_id):
        encoded = value.encode("utf-8")
        material.extend(_write_7bit_length(len(encoded)))
        material.extend(encoded)
    material.extend(struct.pack("<i", len(points)))
    for index, raw in enumerate(points):
        point = db_geometry._require_mapping(raw, f"advisory polygon point {index}")
        x = family_ocr._require_number(point.get("x"), f"advisory polygon point {index} x")
        y = family_ocr._require_number(point.get("y"), f"advisory polygon point {index} y")
        if not math.isfinite(x) or not math.isfinite(y):
            raise EvidenceError("advisory polygon points must be finite")
        material.extend(struct.pack("<d", x))
        material.extend(struct.pack("<d", y))
    return str(UUID(bytes_le=sha256(material).digest()[:16]))


def _validate_advisory_mapping(
    raw_regions: Sequence[Mapping[str, Any]],
    contours: Mapping[str, Mapping[str, Any]],
    final_regions: Sequence[Mapping[str, Any]],
    region_failures: Sequence[Mapping[str, Any]],
) -> tuple[dict[str, Mapping[str, Any]], list[Mapping[str, Any]], int]:
    raw_by_id = {str(region.get("region_id")): region for region in raw_regions}
    if len(raw_by_id) != len(raw_regions) or not all(raw_by_id):
        raise EvidenceError("advisory raw region IDs must be nonempty and unique")
    if set(contours) != set(raw_by_id):
        raise EvidenceError("advisory raw IDs differ from authenticated accepted DB contours")
    expected: dict[str, str] = {}
    sort_keys: dict[str, tuple[float, float, str]] = {}
    for raw_id, raw in raw_by_id.items():
        contour = contours[raw_id]
        if (
            contour.get("expanded_polygon") != raw.get("polygon")
            or contour.get("detection_confidence") != raw.get("detection_confidence")
        ):
            raise EvidenceError("advisory raw geometry or confidence differs from its atomic DB contour")
        initial = db_geometry._require_mapping(
            contour.get("initial_polygon"), "advisory initial DB contour")
        output_id = _advisory_region_id(raw_id, initial)
        if output_id in expected:
            raise EvidenceError("advisory deterministic output IDs are not unique")
        expected[output_id] = raw_id
        points = db_geometry._require_list(
            initial.get("points"), "advisory initial polygon points")
        coordinates = [
            (
                family_ocr._require_number(point.get("x"), "advisory initial point x"),
                family_ocr._require_number(point.get("y"), "advisory initial point y"),
            )
            for point in (
                db_geometry._require_mapping(value, "advisory initial point")
                for value in points)
        ]
        sort_keys[output_id] = (
            min(y for _, y in coordinates), min(x for x, _ in coordinates), output_id)
    final_by_id = {str(region.get("region_id")): region for region in final_regions}
    if len(final_by_id) != len(final_regions):
        raise EvidenceError("advisory final region IDs are not unique")
    failure_ids: set[str] = set()
    for index, raw_failure in enumerate(region_failures):
        failure = db_geometry._require_mapping(raw_failure, f"advisory region failure {index}")
        identifier = str(failure.get("region_id"))
        if not identifier or identifier in failure_ids:
            raise EvidenceError("advisory region failure IDs must be nonempty and unique")
        failure_ids.add(identifier)
    if set(final_by_id).intersection(failure_ids):
        raise EvidenceError("advisory region cannot be both recognized and failed")
    if set(final_by_id) | failure_ids != set(expected):
        raise EvidenceError("advisory output and visible recognition failures omit or add an atomic DB contour")
    expected_order = sorted(expected, key=sort_keys.__getitem__)
    # The detector returns top/left/ID order. OcrPipeline.BuildRegions then
    # deliberately serializes successful OcrRegion records in ordinal ID order.
    expected_success_order = sorted(
        identifier for identifier in expected if identifier not in failure_ids)
    if list(final_by_id) != expected_success_order:
        raise EvidenceError(
            "advisory successful OCR regions are not the ordered detector subsequence")
    by_raw: dict[str, Mapping[str, Any]] = {}
    detector_regions: list[Mapping[str, Any]] = []
    for output_id in expected_order:
        raw_id = expected[output_id]
        initial_polygon = contours[raw_id].get("initial_polygon")
        final = final_by_id.get(output_id)
        if final is not None:
            if final.get("polygon") != initial_polygon:
                raise EvidenceError("advisory output geometry differs from its accepted initial DB contour")
            if final.get("coordinate_space") != "original_pixels":
                raise EvidenceError("advisory output changed the original-pixel coordinate space")
            by_raw[raw_id] = final
        raw = raw_by_id[raw_id]
        detector_regions.append({
            **raw,
            "region_id": output_id,
            "polygon": initial_polygon,
        })
    return by_raw, detector_regions, len(failure_ids)


def _expected_adapter_id(baseline_adapter: Any) -> str:
    if not isinstance(baseline_adapter, str):
        raise EvidenceError("baseline OCR adapter ID is missing")
    token = initial_scorer.INITIAL_COMPOSITION
    if baseline_adapter.count(token) != 1:
        raise EvidenceError("baseline OCR adapter has an unexpected composition")
    return baseline_adapter.replace(token, ADVISORY_COMPOSITION)


def _validate_report_identity(
    report: Mapping[str, Any], baseline_report: Mapping[str, Any], protocol_sha: str,
) -> None:
    if (
        report.get("structure_admission") != "advisory"
        or family_ocr._require_sha256(
            report.get("admission_protocol_sha256"), "admission protocol SHA-256")
        != protocol_sha
        or report.get("ocr_adapter_id") != _expected_adapter_id(
            baseline_report.get("ocr_adapter_id"))
    ):
        raise EvidenceError("advisory runtime report identity is invalid")
    if family_ocr._require_sha256(
        report.get("geometry_protocol_sha256"), "geometry protocol SHA-256"
    ) != initial_scorer.EXPECTED_PROTOCOL_SHA256:
        raise EvidenceError("advisory report changed the initial-contour geometry protocol")


def _validate_baseline_report_identity(report: Mapping[str, Any]) -> None:
    adapter = report.get("ocr_adapter_id")
    if (
        not isinstance(adapter, str)
        or adapter.count(initial_scorer.INITIAL_COMPOSITION) != 1
        or family_ocr._require_sha256(
            report.get("geometry_protocol_sha256"), "baseline geometry protocol SHA-256")
        != initial_scorer.EXPECTED_PROTOCOL_SHA256
        or "structure_admission" in report
        or "admission_protocol_sha256" in report
    ):
        raise EvidenceError("frozen initial-contour report identity is invalid")


def _descriptive_changes(
    baseline_panel: Mapping[str, Any],
    raw_regions: Sequence[Mapping[str, Any]],
    contours: Mapping[str, Mapping[str, Any]],
    advisory_by_raw: Mapping[str, Mapping[str, Any]],
) -> dict[str, int]:
    diagnostic = db_geometry._require_mapping(
        baseline_panel.get("ocr_proposal_diagnostic"), "baseline proposal diagnostic")
    components = [
        db_geometry._require_mapping(value, "baseline component")
        for value in db_geometry._require_list(
            diagnostic.get("component_regions"), "baseline component regions")
    ]
    pairs = initial_scorer._consensus_pairs(raw_regions, components)
    baseline_final = [
        db_geometry._require_mapping(value, "baseline final region")
        for value in db_geometry._require_list(
            baseline_panel.get("ocr", {}).get("regions"), "baseline final regions")
    ]
    baseline_by_id = {str(region.get("region_id")): region for region in baseline_final}
    selected_raw_ids = {str(raw.get("region_id")) for raw, _ in pairs}
    expected_baseline_ids: set[str] = set()
    compared = text = alternatives = roles = confidence = 0
    for raw, component in pairs:
        raw_id = str(raw.get("region_id"))
        expected_id = initial_scorer._initial_output_region_id(
            raw_id,
            str(component.get("region_id")),
            db_geometry._require_mapping(
                contours[raw_id].get("initial_polygon"), "baseline initial polygon"))
        expected_baseline_ids.add(expected_id)
        baseline = baseline_by_id.get(expected_id)
        if baseline is None:
            raise EvidenceError("baseline initial output differs from its selected atomic contour")
        current = advisory_by_raw.get(raw_id)
        if current is None:
            continue
        compared += 1
        text += int(current.get("text") != baseline.get("text"))
        alternatives += int(current.get("alternatives") != baseline.get("alternatives"))
        roles += int(current.get("role") != baseline.get("role"))
        confidence += int(current.get("confidence") != baseline.get("confidence"))
    if set(baseline_by_id) != expected_baseline_ids:
        raise EvidenceError("baseline selected region count differs from deterministic consensus pairs")
    return {
        "baseline_selected_compared": compared,
        "baseline_selected_recognition_failures": len(pairs) - compared,
        "newly_admitted_recognized": sum(
            raw_id not in selected_raw_ids
            for raw_id in advisory_by_raw),
        "text_changed": text,
        "alternatives_changed": alternatives,
        "role_changed_unscored": roles,
        "confidence_changed_unscored": confidence,
    }


def _validate_completed_panel(
    original_panel: Mapping[str, Any],
    baseline_panel: Mapping[str, Any],
    current_panel: Mapping[str, Any],
    db_panel: Mapping[str, Any],
    db_source: Mapping[str, Any],
    db_report_path: Path,
    baseline_candidate: Mapping[str, Any],
    current_candidate: Mapping[str, Any],
) -> tuple[tuple[Any, ...], tuple[Any, ...], tuple[Any, ...], dict[str, int]]:
    initial_scorer._validate_panel_provenance(baseline_panel, current_panel)
    baseline_raw_components, baseline_final_components, _ = initial_scorer._validate_completed_panel(
        original_panel, baseline_panel, db_panel, db_source, db_report_path,
        baseline_candidate)
    baseline_diagnostic = db_geometry._require_mapping(
        baseline_panel.get("ocr_proposal_diagnostic"), "baseline proposal diagnostic")
    current_diagnostic = db_geometry._require_mapping(
        current_panel.get("ocr_proposal_diagnostic"), "advisory proposal diagnostic")
    unchanged = (
        "model_regions", "unmasked_model_regions", "component_regions",
        "detector_input_sha256", "unmasked_input_sha256",
    )
    if any(baseline_diagnostic.get(field) != current_diagnostic.get(field) for field in unchanged):
        raise EvidenceError("advisory run changed raw learned or structure evidence")
    raw_regions = [
        db_geometry._require_mapping(value, "advisory raw region")
        for value in db_geometry._require_list(
            current_diagnostic.get("unmasked_model_regions"), "advisory raw regions")
    ]
    sidecar = initial_scorer._sidecar_value(db_panel, db_source, db_report_path)
    contours = initial_scorer._unmasked_contours(sidecar)
    ocr = db_geometry._require_mapping(current_panel.get("ocr"), "advisory OCR output")
    final_regions = [
        db_geometry._require_mapping(value, "advisory final region")
        for value in db_geometry._require_list(ocr.get("regions"), "advisory final regions")
    ]
    failures = db_geometry._require_list(ocr.get("region_failures"), "advisory region failures")
    cache = db_geometry._require_mapping(ocr.get("cache"), "advisory OCR cache")
    crop_count = family_ocr._require_int(
        cache.get("crop_count"), "advisory crop count", minimum=0)
    if crop_count != len(raw_regions):
        raise EvidenceError("advisory crop count differs from the atomic detector output")
    advisory_by_raw, detector_regions, recognition_failure_count = _validate_advisory_mapping(
        raw_regions, contours, final_regions,
        [db_geometry._require_mapping(value, "advisory region failure") for value in failures])
    masked_sha = family_ocr._require_sha256(
        current_diagnostic.get("detector_input_sha256"), "axis-masked detector input SHA-256")
    original_sha = family_ocr._require_sha256(
        current_diagnostic.get("unmasked_input_sha256"), "original detector input SHA-256")
    original_scorer._runtime_models(
        current_panel, current_candidate, original_sha, masked_sha, "original", bool(raw_regions))
    changes = _descriptive_changes(
        baseline_panel, raw_regions, contours, advisory_by_raw)
    return (
        baseline_raw_components,
        baseline_final_components,
        original_scorer._region_components(
            detector_regions, current_panel, "advisory detector regions"),
        {**changes, "recognition_failures": recognition_failure_count},
    )


def _sum_stats(target: dict[str, int], source: Mapping[str, int]) -> None:
    for key, value in source.items():
        target[key] += value


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
    *,
    evaluator_sha256: str,
) -> dict[str, Any]:
    started = time.perf_counter()
    expected_evaluator = _validate_evaluator_sources(evaluator_sha256)
    output_path = _validate_new_output_path(output_path)
    protocol, protocol_sha, identities = _load_protocol(protocol_path)

    _, baseline_protocol_bytes, baseline_identities = initial_scorer._load_protocol(
        _repository_path(initial_scorer.PROTOCOL_PATH, "initial-contour protocol"))
    if family_ocr._sha256_bytes(baseline_protocol_bytes) != initial_scorer.EXPECTED_PROTOCOL_SHA256:
        raise EvidenceError("initial-contour baseline protocol identity changed")
    baseline_raw, baseline_candidate = original_scorer._validate_complete_candidate(
        _repository_path(BASELINE_CANDIDATE_PATH, "baseline candidate"),
        EXPECTED_BASELINE_CANDIDATE_SHA256)
    current_raw, current_candidate = original_scorer._validate_complete_candidate(
        candidate_path, family_ocr._require_sha256(candidate_sha256, "candidate SHA-256"))
    _validate_isolated_candidate(baseline_raw, current_raw, protocol_sha)
    axis_candidate, original_candidate, original_protocol_sha = _validate_baseline_ancestry(
        baseline_raw, initial_scorer.EXPECTED_PROTOCOL_SHA256)

    baseline_execution_bytes, baseline_assemblies = db_geometry._validate_execution_manifest(
        _repository_path(BASELINE_EXECUTION_PATH, "baseline execution manifest"),
        EXPECTED_BASELINE_EXECUTION_SHA256)
    original_execution_bytes, original_assemblies = db_geometry._validate_execution_manifest(
        _repository_path(
            initial_scorer.ORIGINAL_EXECUTION_PATH, "original-input execution manifest"),
        initial_scorer.EXPECTED_ORIGINAL_EXECUTION_SHA256)
    current_execution_bytes, current_assemblies = db_geometry._validate_execution_manifest(
        execution_manifest_path, execution_manifest_sha256)
    _, db_protocol_bytes, db_bound = db_geometry._load_protocol(
        _repository_path(initial_scorer.DB_PROTOCOL_PATH, "DB geometry protocol"))
    db_protocol_sha = family_ocr._sha256_bytes(db_protocol_bytes)
    db_candidate = db_geometry._validate_candidate(*db_bound["runtime_candidate"])
    db_execution_bytes, db_assemblies = db_geometry._validate_execution_manifest(
        _repository_path(initial_scorer.DB_EXECUTION_PATH, "DB execution manifest"),
        initial_scorer.EXPECTED_DB_EXECUTION_SHA256)
    if db_candidate["sha256"] != axis_candidate["sha256"]:
        raise EvidenceError("DB observer candidate differs from the initial-contour baseline ancestry")

    requested = {
        "train": (train_report_path, train_report_sha256),
        "validation": (dev_report_path, dev_report_sha256),
    }
    report_keys = {"train": "train", "validation": "validation"}
    validated: dict[str, Any] = {}
    all_sources: set[str] = set()
    all_panels: set[str] = set()
    for split in ("train", "validation"):
        prefix = "train" if split == "train" else "dev"
        manifest_path = _repository_path(
            Path(baseline_identities[f"{prefix}_manifest_path"]),
            f"{split} input manifest")
        manifest_sha = family_ocr._require_sha256(
            baseline_identities[f"{prefix}_manifest_sha256"],
            f"{split} input manifest SHA-256")
        if (
            identities[f"{prefix}_manifest_path"]
            != manifest_path.relative_to(REPOSITORY_ROOT).as_posix()
            or identities[f"{prefix}_manifest_sha256"] != manifest_sha
        ):
            raise EvidenceError(f"advisory and initial-contour protocols bind different {split} inputs")
        baseline_report_relative, baseline_report_sha = BASELINE_REPORTS[report_keys[split]]
        original_report_relative, original_report_sha = initial_scorer.ORIGINAL_REPORTS[split]
        original_run = original_scorer._validate_report(
            split, manifest_path, manifest_sha,
            _repository_path(original_report_relative, f"{split} original-input report"),
            original_report_sha, original_candidate, "original",
            original_protocol_sha, original_assemblies)
        baseline_run = original_scorer._validate_report(
            split, manifest_path, manifest_sha,
            _repository_path(baseline_report_relative, f"{split} baseline report"),
            baseline_report_sha, baseline_candidate, "original",
            initial_scorer.EXPECTED_ORIGINAL_PROTOCOL_SHA256, baseline_assemblies)
        _validate_baseline_report_identity(baseline_run[2])
        current_run = original_scorer._validate_report(
            split, manifest_path, manifest_sha, requested[split][0], requested[split][1],
            current_candidate, "original", initial_scorer.EXPECTED_ORIGINAL_PROTOCOL_SHA256,
            current_assemblies)
        _validate_report_identity(current_run[2], baseline_run[2], protocol_sha)

        db_report_relative, db_report_sha = initial_scorer.DB_REPORTS[report_keys[split]]
        db_report_path, db_report_bytes = _exact_file(
            db_report_relative, db_report_sha, f"{split} DB geometry report")
        db_run = db_geometry._validate_run(
            split, manifest_path, db_report_path, manifest_sha, db_protocol_sha,
            db_candidate, db_assemblies)
        if family_ocr._sha256_bytes(db_run[3]) != db_report_sha:
            raise EvidenceError(f"{split} DB report changed during validation")

        images, baseline_cases = baseline_run[0], baseline_run[1]
        original_images, original_cases = original_run[0], original_run[1]
        current_images, current_cases = current_run[0], current_run[1]
        db_images, db_cases = db_run[0], db_run[1]
        if (
            images != original_images or images != current_images or images != db_images
            or set(baseline_cases) != set(original_cases)
            or set(baseline_cases) != set(current_cases)
            or set(baseline_cases) != set(db_cases)
        ):
            raise EvidenceError(f"{split} reports do not cover identical source identities")
        source_ids = set(baseline_cases)
        if source_ids.intersection(all_sources):
            raise EvidenceError("train and validation reuse a source identity")
        all_sources.update(source_ids)

        predictions = {
            "raw_expanded": {str(image["image_sha256"]): [] for image in images},
            "baseline_initial": {str(image["image_sha256"]): [] for image in images},
            "advisory_initial": {str(image["image_sha256"]): [] for image in images},
        }
        stats = {
            "failed_sources": 0, "failed_panels": 0, "completed_panels": 0,
            "raw_contours": 0, "baseline_selected": 0, "advisory_selected": 0,
            "baseline_selected_compared": 0, "newly_admitted_recognized": 0,
            "baseline_selected_recognition_failures": 0,
            "recognition_failures": 0,
            "text_changed": 0, "alternatives_changed": 0,
            "role_changed_unscored": 0, "confidence_changed_unscored": 0,
        }
        baseline_panel_count = 0
        for image in images:
            source_sha = str(image["image_sha256"])
            baseline_source = baseline_cases[source_sha]
            original_source = original_cases[source_sha]
            current_source = current_cases[source_sha]
            db_source = db_cases[source_sha]
            baseline_panels = original_scorer._panel_map(baseline_source, "baseline source")
            original_panels = original_scorer._panel_map(original_source, "original-input source")
            db_panels = original_scorer._panel_map(db_source, "DB source")
            if set(baseline_panels) != set(original_panels) or set(baseline_panels) != set(db_panels):
                raise EvidenceError("original-input, baseline, and DB reports use different panel crops")
            baseline_panel_count += len(baseline_panels)
            if current_source.get("status") == "failed":
                stats["failed_sources"] += 1
                continue
            current_panels = original_scorer._panel_map(current_source, "advisory source")
            if set(current_panels) != set(baseline_panels):
                raise EvidenceError("completed advisory source changed panel crops")
            for crop, baseline_panel in baseline_panels.items():
                current_panel = current_panels[crop]
                panel_id = str(current_panel.get("panel_id"))
                if panel_id in all_panels:
                    raise EvidenceError("train and validation reuse a panel identity")
                all_panels.add(panel_id)
                if current_panel.get("status") != "seed-completed":
                    stats["failed_panels"] += 1
                    continue
                raw, baseline_final, advisory_final, changes = _validate_completed_panel(
                    original_panels[crop], baseline_panel, current_panel,
                    db_panels[crop], db_source,
                    db_report_path, baseline_candidate, current_candidate)
                predictions["raw_expanded"][source_sha].extend(raw)
                predictions["baseline_initial"][source_sha].extend(baseline_final)
                predictions["advisory_initial"][source_sha].extend(advisory_final)
                stats["completed_panels"] += 1
                stats["raw_contours"] += len(raw)
                stats["baseline_selected"] += len(baseline_final)
                stats["advisory_selected"] += len(advisory_final)
                _sum_stats(stats, changes)
        if baseline_panel_count != EXPECTED_COUNTS[split]["panels"]:
            raise EvidenceError(f"{split} panel denominator differs from the advisory protocol")
        validated[split] = {
            "images": images, "dataset_seed": baseline_run[4], "predictions": predictions,
            "stats": stats, "manifest_path": manifest_path, "manifest_sha": manifest_sha,
            "baseline_report_path": _repository_path(
                baseline_report_relative, f"{split} baseline report"),
            "baseline_report_sha": baseline_report_sha,
            "current_report_path": requested[split][0], "current_report_bytes": current_run[3],
            "db_report_path": db_report_path, "db_report_sha": db_report_sha,
        }

    # Every runtime report, artifact, candidate, executable, old contour sidecar,
    # and imported scorer is bound before generator dependencies and truth are read.
    truth_environment = initial_scorer._validate_truth_environment()
    metrics = {stage: {} for stage in ("raw_expanded", "baseline_initial", "advisory_initial")}
    combined_images: list[Mapping[str, Any]] = []
    combined_truths: dict[str, Sequence[Any]] = {}
    combined_predictions = {stage: {} for stage in metrics}
    aggregate_stats = {key: 0 for key in next(iter(validated.values()))["stats"]}
    runs: dict[str, Any] = {}
    total_truth = 0
    for split in ("train", "validation"):
        current = validated[split]
        regenerated = family_ocr._regenerate(
            split, current["dataset_seed"], current["images"])
        truths = {
            str(image["image_sha256"]): family_ocr._truth_regions(
                regenerated[str(image["image_sha256"])][1])[0]
            for image in current["images"]
        }
        truth_count = sum(len(value) for value in truths.values())
        if truth_count != EXPECTED_COUNTS[split]["truths"]:
            raise EvidenceError(f"{split} truth count differs from the reviewed denominator")
        total_truth += truth_count
        for stage in metrics:
            metrics[stage][split] = original_scorer._metrics(
                current["images"], truths, current["predictions"][stage])
            combined_predictions[stage].update(current["predictions"][stage])
        combined_images.extend(current["images"])
        combined_truths.update(truths)
        _sum_stats(aggregate_stats, current["stats"])
        runs[split] = {
            "manifest": {
                "path": current["manifest_path"].relative_to(REPOSITORY_ROOT).as_posix(),
                "sha256": current["manifest_sha"],
            },
            "baseline_report": {
                "path": current["baseline_report_path"].relative_to(REPOSITORY_ROOT).as_posix(),
                "sha256": current["baseline_report_sha"],
            },
            "advisory_report": {
                "path": current["current_report_path"].resolve().relative_to(
                    REPOSITORY_ROOT.resolve()).as_posix(),
                "sha256": family_ocr._sha256_bytes(current["current_report_bytes"]),
            },
            "db_geometry_report": {
                "path": current["db_report_path"].relative_to(REPOSITORY_ROOT).as_posix(),
                "sha256": current["db_report_sha"],
            },
            "source_count": len(current["images"]), "truth_region_count": truth_count,
            **current["stats"],
        }
    if total_truth != EXPECTED_TOTAL_TRUTHS:
        raise EvidenceError("combined truth count differs from the fixed 329-region denominator")
    for stage in metrics:
        metrics[stage]["combined"] = original_scorer._metrics(
            combined_images, combined_truths, combined_predictions[stage])
    if aggregate_stats["advisory_selected"] != aggregate_stats["raw_contours"]:
        raise EvidenceError("advisory output count differs from authenticated raw DB contour count")
    complete_runtime_identity = (
        aggregate_stats["failed_sources"] == 0
        and aggregate_stats["failed_panels"] == 0
        and aggregate_stats["advisory_selected"] == aggregate_stats["raw_contours"])

    result = {
        "schema": OUTPUT_SCHEMA,
        "status": "diagnostic_only_unapproved",
        "scope": "local-synthetic-train-dev-advisory-structure-diagnostic",
        "model_input": "original",
        "model_input_protocol_sha256": initial_scorer.EXPECTED_ORIGINAL_PROTOCOL_SHA256,
        "ocr_output_geometry": "initial_db_contour",
        "geometry_protocol_sha256": initial_scorer.EXPECTED_PROTOCOL_SHA256,
        "structure_admission": "advisory",
        "admission_protocol_sha256": protocol_sha,
        "synthetic_only": True,
        "private_data": False,
        "sealed_data": False,
        "sealed_runs": 0,
        "optimizer_steps": 0,
        "production_approval": False,
        "release_eligible": False,
        "protocol": {
            "path": PROTOCOL_PATH.as_posix(), "sha256": protocol_sha,
            "hypothesis": protocol["hypothesis"], "isolated_change": protocol["isolated_change"],
            "acceptance_bar_reference": protocol["acceptance_bar"],
        },
        "candidate": {
            "path": candidate_path.resolve().relative_to(REPOSITORY_ROOT.resolve()).as_posix(),
            **current_candidate,
        },
        "baseline": {
            "candidate": {"path": BASELINE_CANDIDATE_PATH.as_posix(), **baseline_candidate},
            "execution_manifest": {
                "path": BASELINE_EXECUTION_PATH.as_posix(),
                "sha256": family_ocr._sha256_bytes(baseline_execution_bytes),
            },
        },
        "supporting_evidence": {
            "initial_contour_scorer": {
                "path": INITIAL_SCORER_PATH.as_posix(),
                "sha256": EXPECTED_INITIAL_SCORER_SHA256,
            },
            "db_geometry_execution_manifest": {
                "path": initial_scorer.DB_EXECUTION_PATH.as_posix(),
                "sha256": family_ocr._sha256_bytes(db_execution_bytes),
            },
            "original_input_execution_manifest": {
                "path": initial_scorer.ORIGINAL_EXECUTION_PATH.as_posix(),
                "sha256": family_ocr._sha256_bytes(original_execution_bytes),
            },
        },
        "execution_manifest": {
            "path": execution_manifest_path.resolve().relative_to(REPOSITORY_ROOT.resolve()).as_posix(),
            "sha256": family_ocr._sha256_bytes(current_execution_bytes),
            "all_declared_files_verified": True,
            "runtime_assemblies": [
                {"name": name, "sha256": value} for name, value in current_assemblies
            ],
        },
        "runs": runs,
        "integrity": {
            "candidate_diff_is_admission_only": True,
            "source_panel_and_runtime_artifacts_verified_before_truth": True,
            "imported_initial_scorer_authenticated_before_use": True,
            "truth_dependencies_authenticated_before_regeneration": True,
            "old_db_reports_and_every_contour_sidecar_verified_before_truth": True,
            "completed_panel_raw_model_ids_expanded_geometry_and_confidence_unchanged": True,
            "every_accepted_atomic_initial_contour_emitted_exactly_once": complete_runtime_identity,
            "completed_panel_successful_region_order_directly_observed": True,
            "orientation_confidence_and_advisory_evidence_directly_observed": False,
            "orientation_confidence_and_advisory_evidence_source_backed": True,
            "full_source_truth_count": total_truth,
            "complete_identity_verification": complete_runtime_identity,
            "final_recognition_trace_complete": aggregate_stats["recognition_failures"] == 0,
            **aggregate_stats,
        },
        "evaluator": {
            "path": "tools/GraphReader.SyntheticRuntimeEvidence/score_advisory_structure.py",
            "sha256": expected_evaluator,
            "reviewed_bytes_authenticated_before_truth": True,
            "matching": "existing maximum-cardinality one-to-one IoU matching",
            "intersection_over_union_minimum": family_ocr.MATCH_IOU_MINIMUM,
            "truth_denominator": "all 146 train plus 183 development regions including failed cases",
            "recognition_scored": False,
            "roles_scored": False,
        },
        "detection_metrics": metrics,
        "recognition_and_role_changes": {
            "baseline_selected_compared": aggregate_stats["baseline_selected_compared"],
            "baseline_selected_recognition_failures": (
                aggregate_stats["baseline_selected_recognition_failures"]),
            "newly_admitted_regions_recognized": aggregate_stats["newly_admitted_recognized"],
            "recognition_failure_count": aggregate_stats["recognition_failures"],
            "text_changed_count": aggregate_stats["text_changed"],
            "alternatives_changed_count": aggregate_stats["alternatives_changed"],
            "role_changed_count_unscored": aggregate_stats["role_changed_unscored"],
            "confidence_changed_count_unscored": aggregate_stats["confidence_changed_unscored"],
            "interpretation": "Recognition and role fields are descriptive changes only and receive no correctness credit.",
        },
        "truth_environment": truth_environment,
        "limitations": [
            "Synthetic train/dev evidence cannot approve a model, production stage, or release.",
            "Recognition strings and roles are not scored by this protocol.",
            "Structure associations remain diagnostic evidence and do not establish semantic correctness.",
            "The runtime report does not serialize detector orientation, detection confidence, or advisory evidence reasons; preservation of those fields is backed by the authenticated executable source rather than directly observed telemetry.",
        ],
        "elapsed_milliseconds": (time.perf_counter() - started) * 1000.0,
    }
    output_path.parent.mkdir(parents=True, exist_ok=True)
    payload = (json.dumps(result, indent=2, sort_keys=True) + "\n").encode("utf-8")
    with output_path.open("xb") as stream:
        stream.write(payload)
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("protocol", type=Path)
    parser.add_argument("candidate", type=Path)
    parser.add_argument("candidate_sha256")
    parser.add_argument("execution_manifest", type=Path)
    parser.add_argument("execution_manifest_sha256")
    parser.add_argument("train_report", type=Path)
    parser.add_argument("train_report_sha256")
    parser.add_argument("dev_report", type=Path)
    parser.add_argument("dev_report_sha256")
    parser.add_argument("output", type=Path)
    parser.add_argument("--evaluator-sha256", required=True)
    arguments = parser.parse_args()
    score(
        arguments.protocol, arguments.candidate, arguments.candidate_sha256,
        arguments.execution_manifest, arguments.execution_manifest_sha256,
        arguments.train_report, arguments.train_report_sha256,
        arguments.dev_report, arguments.dev_report_sha256, arguments.output,
        evaluator_sha256=arguments.evaluator_sha256)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
