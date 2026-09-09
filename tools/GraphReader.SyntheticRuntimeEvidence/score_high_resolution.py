# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Score the authenticated 1920-side OCR detector experiment."""

from __future__ import annotations

import argparse
from copy import deepcopy
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

import score_advisory_structure as advisory_scorer  # noqa: E402


initial_scorer = advisory_scorer.initial_scorer
original_scorer = advisory_scorer.original_scorer
db_geometry = advisory_scorer.db_geometry
family_ocr = advisory_scorer.family_ocr
EvidenceError = advisory_scorer.EvidenceError

PROTOCOL_PATH = Path("ml/ocr/official_bakeoff/high_resolution_dev_protocol.json")
EXPECTED_PROTOCOL_SHA256 = "91ec2252f7e51d215fcd8e05335de9d786665d29e1361f1d3a904d5c7bda0ea8"
BASELINE_CANDIDATE_PATH = Path(
    "artifacts/synthetic-runtime-evidence/advisory-structure-source-c96/candidate.json")
EXPECTED_BASELINE_CANDIDATE_SHA256 = (
    "eed14ce421d56b8251213043337b2867ab7f96410404544341e512b27ffabbdd")
BASELINE_SCORE_PATH = Path(
    "artifacts/synthetic-runtime-evidence/advisory-structure-score-run1.json")
EXPECTED_BASELINE_SCORE_SHA256 = (
    "a3c2d913245acba7fbb5c17eac29adf96fc3368583af4c08f6fda548c2f2d154")
ADVISORY_SCORER_PATH = Path(
    "tools/GraphReader.SyntheticRuntimeEvidence/score_advisory_structure.py")
EXPECTED_ADVISORY_SCORER_SHA256 = (
    "95d9197f8a09032dcf7f59989b81455b5e2252d9b306885aefe9384c1d6b1fc7")
MAXIMUM_SIDE_LENGTH = 1920
DIMENSION_MULTIPLE = 128
ADAPTER_SUFFIX = "-detector-max-1920"
OUTPUT_SCHEMA = "graphreader.synthetic-high-resolution-score.v1"
EXPECTED_COUNTS = {
    "train": {"sources": 4, "panels": 4, "truths": 146},
    "validation": {"sources": 3, "panels": 9, "truths": 183},
}
EXPECTED_TOTAL_TRUTHS = 329
PROTOCOL_KEYS = {
    "evidence_policy", "hypothesis", "isolated_change", "split_identities",
    "metric", "acceptance_bar", "budget",
}
IDENTITY_KEYS = {
    "baseline_candidate_path", "baseline_candidate_sha256",
    "baseline_protocol_path", "baseline_protocol_sha256",
    "baseline_score_path", "baseline_score_sha256",
    "train_manifest_path", "train_manifest_sha256", "dev_manifest_path",
    "dev_manifest_sha256", "train_source_count", "train_panel_count",
    "train_truth_count", "dev_source_count", "dev_panel_count", "dev_truth_count",
}


def _repository_path(relative: Path, label: str) -> Path:
    if relative.is_absolute():
        raise EvidenceError(f"{label} must be repository-relative")
    root = REPOSITORY_ROOT.resolve()
    path = (root / relative).resolve()
    if path == root or root not in path.parents:
        raise EvidenceError(f"{label} escaped the repository")
    return path


def _local_sha256(value: Any, label: str) -> str:
    if (
        not isinstance(value, str)
        or len(value) != 64
        or value != value.lower()
        or any(character not in "0123456789abcdef" for character in value)
    ):
        raise EvidenceError(f"{label} must be a lowercase SHA-256")
    return value


def _exact_file(relative: Path, expected_sha256: str, label: str) -> tuple[Path, bytes]:
    path = _repository_path(relative, label)
    if not path.is_file():
        raise EvidenceError(f"{label} is missing")
    payload = path.read_bytes()
    if sha256(payload).hexdigest() != _local_sha256(expected_sha256, f"{label} SHA-256"):
        raise EvidenceError(f"{label} bytes differ from the fixed identity")
    return path, payload


def _validate_evaluator_sources(evaluator_sha256: str) -> str:
    expected = _local_sha256(evaluator_sha256, "reviewed high-resolution evaluator SHA-256")
    if sha256(Path(__file__).read_bytes()).hexdigest() != expected:
        raise EvidenceError("current high-resolution evaluator bytes differ from the reviewed identity")
    _exact_file(ADVISORY_SCORER_PATH, EXPECTED_ADVISORY_SCORER_SHA256, "advisory scorer")
    advisory_scorer._validate_evaluator_sources(EXPECTED_ADVISORY_SCORER_SHA256)
    return expected


def _validate_new_output_path(path: Path) -> Path:
    root = REPOSITORY_ROOT.resolve()
    artifacts = (root / "artifacts").resolve()
    output = path.resolve()
    if output == artifacts or artifacts not in output.parents:
        raise EvidenceError("high-resolution score output must stay under repository artifacts")
    if output.exists():
        raise EvidenceError("high-resolution score output must use a new destination")
    return output


def _load_protocol(path: Path) -> tuple[dict[str, Any], str, dict[str, Any]]:
    expected_path = _repository_path(PROTOCOL_PATH, "high-resolution protocol")
    if path.resolve() != expected_path:
        raise EvidenceError("high-resolution scoring requires the reviewed repository protocol path")
    protocol, payload = family_ocr._load_object(expected_path, "high-resolution protocol")
    digest = family_ocr._sha256_bytes(payload)
    if digest != EXPECTED_PROTOCOL_SHA256:
        raise EvidenceError("high-resolution protocol bytes differ from the reviewed identity")
    family_ocr._require_exact_keys(protocol, PROTOCOL_KEYS, "high-resolution protocol")
    if (
        protocol.get("evidence_policy") != "ml/policy/evidence-policy.json"
        or not all(isinstance(protocol.get(field), str) and protocol[field].strip()
                   for field in ("hypothesis", "isolated_change", "metric", "acceptance_bar"))
    ):
        raise EvidenceError("high-resolution protocol metadata is invalid")
    budget = db_geometry._require_mapping(protocol.get("budget"), "high-resolution budget")
    family_ocr._require_exact_keys(
        budget,
        {"synthetic_train_dev_runs", "optimizer_steps", "private_reads", "sealed_runs",
         "production_approval", "release_eligible"},
        "high-resolution budget")
    if budget != {
        "synthetic_train_dev_runs": "unlimited", "optimizer_steps": 0,
        "private_reads": 0, "sealed_runs": 0, "production_approval": False,
        "release_eligible": False,
    }:
        raise EvidenceError("high-resolution protocol cannot authorize restricted use")
    identities = dict(db_geometry._require_mapping(
        protocol.get("split_identities"), "high-resolution split identities"))
    family_ocr._require_exact_keys(identities, IDENTITY_KEYS, "high-resolution split identities")
    expected_fixed = {
        "baseline_candidate_path": BASELINE_CANDIDATE_PATH.as_posix(),
        "baseline_candidate_sha256": EXPECTED_BASELINE_CANDIDATE_SHA256,
        "baseline_protocol_path": advisory_scorer.PROTOCOL_PATH.as_posix(),
        "baseline_protocol_sha256": advisory_scorer.EXPECTED_PROTOCOL_SHA256,
        "baseline_score_path": BASELINE_SCORE_PATH.as_posix(),
        "baseline_score_sha256": EXPECTED_BASELINE_SCORE_SHA256,
    }
    if any(identities[key] != value for key, value in expected_fixed.items()):
        raise EvidenceError("high-resolution protocol does not bind the frozen advisory baseline")
    for split, prefix in (("train", "train"), ("validation", "dev")):
        expected = EXPECTED_COUNTS[split]
        if any(identities[f"{prefix}_{name}_count"] != expected[name + "s"]
               for name in ("source", "panel", "truth")):
            raise EvidenceError(f"high-resolution {split} denominator differs from the protocol")
    return protocol, digest, identities


def _validate_isolated_candidate(
    baseline: Mapping[str, Any], candidate: Mapping[str, Any], protocol_sha256: str,
) -> None:
    maximum = candidate.get("ocr_detector_maximum_side_length")
    if type(maximum) is not int or maximum != MAXIMUM_SIDE_LENGTH:
        raise EvidenceError("candidate does not select the reviewed detector maximum side")
    if candidate.get("detector_resolution_protocol") != {
        "path": PROTOCOL_PATH.as_posix(), "sha256": protocol_sha256,
    }:
        raise EvidenceError("candidate does not bind the reviewed resolution protocol")
    normalized = deepcopy(dict(candidate))
    normalized.pop("ocr_detector_maximum_side_length", None)
    normalized.pop("detector_resolution_protocol", None)
    if normalized != baseline:
        raise EvidenceError("candidate changes more than detector resolution and its protocol")


def _validate_baseline_score() -> tuple[dict[str, Any], bytes]:
    _, payload = _exact_file(
        BASELINE_SCORE_PATH, EXPECTED_BASELINE_SCORE_SHA256, "advisory baseline score")
    try:
        value = json.loads(payload)
    except json.JSONDecodeError as exception:
        raise EvidenceError(f"advisory baseline score is invalid JSON: {exception}") from exception
    score = db_geometry._require_mapping(value, "advisory baseline score")
    candidate = db_geometry._require_mapping(score.get("candidate"), "baseline score candidate")
    integrity = db_geometry._require_mapping(score.get("integrity"), "baseline score integrity")
    if (
        score.get("schema") != advisory_scorer.OUTPUT_SCHEMA
        or score.get("status") != "diagnostic_only_unapproved"
        or score.get("synthetic_only") is not True
        or score.get("private_data") is not False
        or score.get("sealed_data") is not False
        or score.get("production_approval") is not False
        or score.get("release_eligible") is not False
        or candidate.get("sha256") != EXPECTED_BASELINE_CANDIDATE_SHA256
        or score.get("admission_protocol_sha256") != advisory_scorer.EXPECTED_PROTOCOL_SHA256
        or integrity.get("full_source_truth_count") != EXPECTED_TOTAL_TRUTHS
        or integrity.get("complete_identity_verification") is not True
    ):
        raise EvidenceError("advisory baseline score identity or completeness is invalid")
    metrics = db_geometry._require_mapping(score.get("detection_metrics"), "baseline metrics")
    for stage in ("raw_expanded", "baseline_initial", "advisory_initial"):
        stage_metrics = db_geometry._require_mapping(metrics.get(stage), f"baseline {stage} metrics")
        for split in ("train", "validation"):
            split_metrics = db_geometry._require_mapping(
                stage_metrics.get(split), f"baseline {stage} {split} metrics")
            if split_metrics.get("truth_region_count") != EXPECTED_COUNTS[split]["truths"]:
                raise EvidenceError("advisory baseline score denominator changed")
        if db_geometry._require_mapping(
            stage_metrics.get("combined"), f"baseline {stage} combined metrics"
        ).get("truth_region_count") != EXPECTED_TOTAL_TRUTHS:
            raise EvidenceError("advisory baseline combined denominator changed")
    return dict(score), payload


def _expected_adapter_id(baseline_adapter: Any) -> str:
    if not isinstance(baseline_adapter, str) or baseline_adapter.count(ADVISORY_SUFFIX_TOKEN) != 1:
        raise EvidenceError("baseline advisory adapter ID has an unexpected composition")
    return baseline_adapter.replace(ADVISORY_SUFFIX_TOKEN, ADVISORY_SUFFIX_TOKEN + ADAPTER_SUFFIX)


ADVISORY_SUFFIX_TOKEN = advisory_scorer.ADVISORY_COMPOSITION


def _expected_tensor_dimensions(width: int, height: int) -> tuple[int, int]:
    source_width, source_height = (
        (max(32, width), max(32, height)) if width + height < 64 else (width, height))
    ratio = MAXIMUM_SIDE_LENGTH / max(source_width, source_height)
    resized_width = int(source_width * ratio)
    resized_height = int(source_height * ratio)
    if resized_width <= 0 or resized_height <= 0:
        raise EvidenceError("high-resolution DB resize produced a zero tensor dimension")
    align = lambda value: ((value + DIMENSION_MULTIPLE - 1) // DIMENSION_MULTIPLE) * DIMENSION_MULTIPLE
    return align(resized_width), align(resized_height)


def _candidate_adapter_id(candidate: Mapping[str, Any]) -> str:
    detector = db_geometry._require_mapping(candidate.get("detector"), "candidate detector")
    recognizer = db_geometry._require_mapping(candidate.get("recognizer"), "candidate recognizer")
    return ":".join((
        "graphreader-ocr",
        ADVISORY_SUFFIX_TOKEN + ADAPTER_SUFFIX,
        family_ocr._require_sha256(detector.get("model_sha256"), "detector SHA-256")[:12],
        family_ocr._require_sha256(recognizer.get("model_sha256"), "recognizer SHA-256")[:12],
        family_ocr._require_sha256(candidate.get("native_sha256"), "native SHA-256")[:12],
    ))


def _validate_report_identity(
    report: Mapping[str, Any], candidate: Mapping[str, Any] | None = None,
) -> None:
    adapter = report.get("ocr_adapter_id")
    maximum = report.get("detector_maximum_side_length")
    if (
        report.get("model_input") != "original"
        or report.get("model_input_protocol_sha256") != initial_scorer.EXPECTED_ORIGINAL_PROTOCOL_SHA256
        or report.get("geometry_protocol_sha256") != initial_scorer.EXPECTED_PROTOCOL_SHA256
        or report.get("structure_admission") != "advisory"
        or report.get("admission_protocol_sha256") != advisory_scorer.EXPECTED_PROTOCOL_SHA256
        or type(maximum) is not int
        or maximum != MAXIMUM_SIDE_LENGTH
        or report.get("detector_resolution_protocol_sha256") != EXPECTED_PROTOCOL_SHA256
        or not isinstance(adapter, str)
        or adapter.count(ADVISORY_SUFFIX_TOKEN + ADAPTER_SUFFIX) != 1
        or ADVISORY_SUFFIX_TOKEN + ":" in adapter
        or (candidate is not None and adapter != _candidate_adapter_id(candidate))
    ):
        raise EvidenceError("high-resolution runtime report identity is invalid")


def _validate_current_sidecar(
    panel: Mapping[str, Any], source: Mapping[str, Any], report: Mapping[str, Any],
    report_path: Path, manifest_sha256: str, candidate: Mapping[str, Any],
    assemblies: tuple[tuple[str, str], ...],
) -> tuple[dict[str, Mapping[str, Any]], tuple[Any, ...], dict[str, int]]:
    diagnostic = db_geometry._require_mapping(
        panel.get("ocr_proposal_diagnostic"), "high-resolution proposal diagnostic")
    if diagnostic.get("coordinate_space") != "original_pixels" or diagnostic.get("used_as_accepted_evidence") is not False:
        raise EvidenceError("high-resolution diagnostic coordinate space or evidence use is invalid")
    descriptor = db_geometry._require_mapping(
        diagnostic.get("db_geometry_diagnostic_sidecar"), "high-resolution sidecar descriptor")
    family_ocr._require_exact_keys(
        descriptor, {"file", "sha256", "byte_count"}, "high-resolution sidecar descriptor")
    panel_directory = report_path.parent / str(source["image_sha256"]) / str(panel["panel_id"])
    sidecar_path = family_ocr._require_owned_file(
        panel_directory, descriptor["file"], "high-resolution sidecar",
        repository_root=REPOSITORY_ROOT)
    payload = sidecar_path.read_bytes()
    if (
        family_ocr._sha256_bytes(payload) != family_ocr._require_sha256(
            descriptor["sha256"], "high-resolution sidecar SHA-256")
        or len(payload) != family_ocr._require_int(
            descriptor["byte_count"], "high-resolution sidecar byte count", minimum=1)
    ):
        raise EvidenceError("high-resolution sidecar bytes differ from its descriptor")
    try:
        sidecar = json.loads(payload)
    except json.JSONDecodeError as exception:
        raise EvidenceError(f"high-resolution sidecar is invalid JSON: {exception}") from exception
    sidecar = db_geometry._require_mapping(sidecar, "high-resolution sidecar")
    if (
        sidecar.get("schema") != db_geometry.SIDECAR_SCHEMA
        or sidecar.get("scope") != db_geometry.SIDECAR_SCOPE
        or sidecar.get("production_approved") is not False
        or sidecar.get("training_input_ready") is not False
        or sidecar.get("truth_used_by_runtime") is not False
    ):
        raise EvidenceError("high-resolution sidecar schema or scientific flags are invalid")
    protocol = db_geometry._require_mapping(sidecar.get("protocol"), "sidecar protocol")
    if protocol != {"path": PROTOCOL_PATH.as_posix(), "sha256": EXPECTED_PROTOCOL_SHA256}:
        raise EvidenceError("high-resolution sidecar does not bind the reviewed protocol")
    if family_ocr._require_sha256(sidecar.get("input_manifest_sha256"), "sidecar manifest SHA-256") != manifest_sha256:
        raise EvidenceError("high-resolution sidecar does not bind the input manifest")
    if family_ocr._require_sha256(sidecar.get("candidate_sha256"), "sidecar candidate SHA-256") != candidate["sha256"]:
        raise EvidenceError("high-resolution sidecar does not bind the candidate")
    if db_geometry._require_mapping(sidecar.get("source"), "sidecar source") != {
        "image_sha256": source["image_sha256"], "width": source["width"], "height": source["height"],
    }:
        raise EvidenceError("high-resolution sidecar source identity differs from the report")
    expected_panel = {
        "panel_id": panel["panel_id"], "image_sha256": panel["image_sha256"],
        "width": panel["width"], "height": panel["height"], "crop": panel["crop"],
        "requested_crop": panel["requested_crop"],
        "source_to_panel_matrix": panel["source_to_panel_matrix"],
        "panel_to_source_matrix": panel["panel_to_source_matrix"],
    }
    if db_geometry._require_mapping(sidecar.get("panel"), "sidecar panel") != expected_panel:
        raise EvidenceError("high-resolution sidecar panel provenance differs from the report")
    if (
        sidecar.get("native_sha256") != candidate["native_sha256"]
        or sidecar.get("native_scope") != candidate["native_scope"]
        or db_geometry._runtime_assemblies(sidecar.get("runtime_assemblies"), "sidecar assemblies") != assemblies
        or db_geometry._require_mapping(sidecar.get("detector_model"), "sidecar detector") != candidate["detector"]
    ):
        raise EvidenceError("high-resolution sidecar runtime identity differs from the candidate")

    width = family_ocr._require_int(panel.get("width"), "panel width", minimum=1)
    height = family_ocr._require_int(panel.get("height"), "panel height", minimum=1)
    expected_tensor = _expected_tensor_dimensions(width, height)
    crop = db_geometry._rect_tuple(panel.get("crop"), "panel crop")
    offset_x, offset_y = int(crop[0]), int(crop[1])
    invocations = db_geometry._require_list(sidecar.get("invocations"), "sidecar invocations")
    if len(invocations) != len(db_geometry.EXPECTED_INVOCATIONS):
        raise EvidenceError("high-resolution sidecar must contain masked and unmasked invocations")
    contours_by_id: dict[str, Mapping[str, Any]] = {}
    unmasked_initial: tuple[Any, ...] = ()
    dimensions: dict[str, int] = {}
    seen: set[tuple[str, str]] = set()
    for index, ((kind, stage), raw_invocation) in enumerate(zip(db_geometry.EXPECTED_INVOCATIONS, invocations)):
        invocation = db_geometry._require_mapping(raw_invocation, f"sidecar invocation {index}")
        if invocation.get("kind") != kind:
            raise EvidenceError("high-resolution sidecar invocation order or kind is invalid")
        gray_field = "detector_input_sha256" if stage == "masked" else "unmasked_input_sha256"
        regions_field = "model_regions" if stage == "masked" else "unmasked_model_regions"
        if family_ocr._require_sha256(invocation.get("canonical_gray_sha256"), f"{stage} gray SHA-256") != family_ocr._require_sha256(diagnostic.get(gray_field), f"report {stage} gray SHA-256"):
            raise EvidenceError(f"{stage} sidecar input differs from the report")
        observation = db_geometry._require_mapping(invocation.get("observation"), f"{stage} observation")
        if family_ocr._require_sha256(observation.get("input_sha256"), f"{stage} observation SHA-256") != family_ocr._require_sha256(invocation.get("detector_bgr_sha256"), f"{stage} BGR SHA-256"):
            raise EvidenceError(f"{stage} observation does not bind detector BGR bytes")
        tensor_width = family_ocr._require_int(observation.get("tensor_width"), f"{stage} tensor width", minimum=1)
        tensor_height = family_ocr._require_int(observation.get("tensor_height"), f"{stage} tensor height", minimum=1)
        if (
            family_ocr._require_int(observation.get("image_width"), f"{stage} image width", minimum=1) != width
            or family_ocr._require_int(observation.get("image_height"), f"{stage} image height", minimum=1) != height
        ):
            raise EvidenceError(f"{stage} observation dimensions differ from the panel")
        if (tensor_width, tensor_height) != expected_tensor:
            raise EvidenceError(
                f"{stage} observation tensor dimensions do not prove the 1920-side DB resize")
        dimensions[f"{stage}_tensor_width"] = tensor_width
        dimensions[f"{stage}_tensor_height"] = tensor_height
        contours = db_geometry._require_list(observation.get("accepted_contours"), f"{stage} contours")
        regions = db_geometry._require_list(diagnostic.get(regions_field), f"report {stage} regions")
        if len(contours) != len(regions):
            raise EvidenceError(f"{stage} contour count differs from report regions")
        initial_components: list[Any] = []
        for contour_index, (raw_contour, raw_region) in enumerate(zip(contours, regions)):
            contour = db_geometry._require_mapping(raw_contour, f"{stage} contour {contour_index}")
            region = db_geometry._require_mapping(raw_region, f"{stage} region {contour_index}")
            identifier = str(contour.get("returned_region_id"))
            if not identifier or (stage, identifier) in seen:
                raise EvidenceError(f"{stage} contour identities must be unique")
            seen.add((stage, identifier))
            initial = db_geometry._polygon_points(
                contour.get("initial_polygon"), f"{stage} initial polygon", width, height)
            db_geometry._validate_report_model_region(
                contour, region, f"{stage} contour {contour_index}", width, height)
            initial_components.append(db_geometry._component(initial, offset_x, offset_y))
            if stage == "unmasked":
                contours_by_id[identifier] = contour
        if stage == "unmasked":
            unmasked_initial = tuple(initial_components)
    return contours_by_id, unmasked_initial, dimensions


def _validate_completed_panel(
    panel: Mapping[str, Any], source: Mapping[str, Any], report: Mapping[str, Any],
    report_path: Path, manifest_sha256: str, candidate: Mapping[str, Any],
    assemblies: tuple[tuple[str, str], ...],
) -> tuple[tuple[Any, ...], tuple[Any, ...], int, dict[str, int]]:
    diagnostic = db_geometry._require_mapping(
        panel.get("ocr_proposal_diagnostic"), "high-resolution proposal diagnostic")
    raw_regions = [
        db_geometry._require_mapping(value, "high-resolution raw region")
        for value in db_geometry._require_list(
            diagnostic.get("unmasked_model_regions"), "high-resolution raw regions")
    ]
    contours, initial_components, dimensions = _validate_current_sidecar(
        panel, source, report, report_path, manifest_sha256, candidate, assemblies)
    ocr = db_geometry._require_mapping(panel.get("ocr"), "high-resolution OCR output")
    final_regions = [
        db_geometry._require_mapping(value, "high-resolution final region")
        for value in db_geometry._require_list(ocr.get("regions"), "high-resolution final regions")
    ]
    failures = [
        db_geometry._require_mapping(value, "high-resolution region failure")
        for value in db_geometry._require_list(ocr.get("region_failures"), "region failures")
    ]
    cache = db_geometry._require_mapping(ocr.get("cache"), "high-resolution OCR cache")
    if family_ocr._require_int(cache.get("crop_count"), "crop count", minimum=0) != len(raw_regions):
        raise EvidenceError("high-resolution crop count differs from raw contours")
    _, detector_regions, failure_count = advisory_scorer._validate_advisory_mapping(
        raw_regions, contours, final_regions, failures)
    masked_sha = family_ocr._require_sha256(
        diagnostic.get("detector_input_sha256"), "masked detector input SHA-256")
    original_sha = family_ocr._require_sha256(
        diagnostic.get("unmasked_input_sha256"), "original detector input SHA-256")
    original_scorer._runtime_models(panel, candidate, original_sha, masked_sha, "original", bool(raw_regions))
    crop = db_geometry._rect_tuple(panel.get("crop"), "panel crop")
    expanded = original_scorer._region_components(
        raw_regions, panel, "high-resolution expanded regions")
    advisory_initial = original_scorer._region_components(
        detector_regions, panel, "high-resolution initial regions")
    if len(initial_components) != len(advisory_initial):
        raise EvidenceError("high-resolution initial geometry count differs from advisory output")
    return expanded, advisory_initial, failure_count, {
        **dimensions,
        "panel_width": int(panel["width"]), "panel_height": int(panel["height"]),
        "source_crop_x": int(crop[0]), "source_crop_y": int(crop[1]),
    }


def score(
    protocol_path: Path, candidate_path: Path, candidate_sha256: str,
    execution_manifest_path: Path, execution_manifest_sha256: str,
    train_report_path: Path, train_report_sha256: str,
    dev_report_path: Path, dev_report_sha256: str,
    output_path: Path, *, evaluator_sha256: str,
) -> dict[str, Any]:
    started = time.perf_counter()
    expected_evaluator = _validate_evaluator_sources(evaluator_sha256)
    output_path = _validate_new_output_path(output_path)
    protocol, protocol_sha, identities = _load_protocol(protocol_path)
    baseline_score, baseline_score_bytes = _validate_baseline_score()
    baseline_raw, baseline_candidate = original_scorer._validate_complete_candidate(
        _repository_path(BASELINE_CANDIDATE_PATH, "baseline candidate"),
        EXPECTED_BASELINE_CANDIDATE_SHA256)
    current_raw, current_candidate = original_scorer._validate_complete_candidate(
        candidate_path, family_ocr._require_sha256(candidate_sha256, "candidate SHA-256"))
    _validate_isolated_candidate(baseline_raw, current_raw, protocol_sha)
    execution_bytes, assemblies = db_geometry._validate_execution_manifest(
        execution_manifest_path, execution_manifest_sha256)

    requested = {
        "train": (train_report_path, train_report_sha256),
        "validation": (dev_report_path, dev_report_sha256),
    }
    validated: dict[str, Any] = {}
    all_sources: set[str] = set()
    all_panels: set[str] = set()
    for split in ("train", "validation"):
        prefix = "train" if split == "train" else "dev"
        manifest_path = _repository_path(
            Path(identities[f"{prefix}_manifest_path"]), f"{split} manifest")
        manifest_sha = family_ocr._require_sha256(
            identities[f"{prefix}_manifest_sha256"], f"{split} manifest SHA-256")
        images, cases, report, report_bytes, dataset_seed, _ = original_scorer._validate_report(
            split, manifest_path, manifest_sha, requested[split][0], requested[split][1],
            current_candidate, "original", initial_scorer.EXPECTED_ORIGINAL_PROTOCOL_SHA256,
            assemblies)
        _validate_report_identity(report, current_candidate)
        runtime_elapsed = family_ocr._require_number(
            report.get("elapsed_milliseconds"), f"{split} whole runtime elapsed milliseconds")
        if not math.isfinite(runtime_elapsed) or runtime_elapsed < 0:
            raise EvidenceError(f"{split} whole runtime elapsed milliseconds must be finite and nonnegative")
        if len(images) != EXPECTED_COUNTS[split]["sources"]:
            raise EvidenceError(f"{split} source denominator differs from the protocol")
        source_ids = set(cases)
        if source_ids.intersection(all_sources):
            raise EvidenceError("train and validation reuse a source identity")
        all_sources.update(source_ids)
        predictions = {
            "raw_expanded": {str(image["image_sha256"]): [] for image in images},
            "advisory_initial": {str(image["image_sha256"]): [] for image in images},
        }
        stats = {"failed_sources": 0, "failed_panels": 0, "completed_panels": 0,
                 "raw_contours": 0, "advisory_selected": 0, "recognition_failures": 0}
        tensor_observations: list[dict[str, int]] = []
        panel_count = 0
        for image in images:
            source_sha = str(image["image_sha256"])
            source = cases[source_sha]
            if source.get("status") == "failed":
                stats["failed_sources"] += 1
            panels = original_scorer._panel_map(source, "high-resolution source")
            panel_count += len(panels)
            for panel in panels.values():
                panel_id = str(panel.get("panel_id"))
                if not panel_id or panel_id in all_panels:
                    raise EvidenceError("high-resolution panel identities are empty or reused")
                all_panels.add(panel_id)
                if panel.get("status") != "seed-completed":
                    stats["failed_panels"] += 1
                    continue
                expanded, initial, failure_count, dimensions = _validate_completed_panel(
                    panel, source, report, requested[split][0], manifest_sha,
                    current_candidate, assemblies)
                predictions["raw_expanded"][source_sha].extend(expanded)
                predictions["advisory_initial"][source_sha].extend(initial)
                stats["completed_panels"] += 1
                stats["raw_contours"] += len(expanded)
                stats["advisory_selected"] += len(initial)
                stats["recognition_failures"] += failure_count
                tensor_observations.append(dimensions)
        if panel_count > EXPECTED_COUNTS[split]["panels"]:
            raise EvidenceError(f"{split} runtime emitted more panels than the fixed denominator")
        validated[split] = {
            "images": images, "dataset_seed": dataset_seed, "predictions": predictions,
            "stats": stats, "tensor_observations": tensor_observations,
            "runtime_elapsed_milliseconds": runtime_elapsed,
            "manifest_path": manifest_path, "manifest_sha": manifest_sha,
            "report_path": requested[split][0], "report_bytes": report_bytes,
        }

    # Truth dependencies are authenticated only after all candidate/runtime/sidecar evidence.
    truth_environment = initial_scorer._validate_truth_environment()
    metrics = {stage: {} for stage in ("raw_expanded", "advisory_initial")}
    combined_images: list[Mapping[str, Any]] = []
    combined_truths: dict[str, Sequence[Any]] = {}
    combined_predictions = {stage: {} for stage in metrics}
    aggregate = {"failed_sources": 0, "failed_panels": 0, "completed_panels": 0,
                 "raw_contours": 0, "advisory_selected": 0, "recognition_failures": 0}
    all_tensors: list[dict[str, int]] = []
    runs: dict[str, Any] = {}
    total_truth = 0
    for split in ("train", "validation"):
        current = validated[split]
        regenerated = family_ocr._regenerate(split, current["dataset_seed"], current["images"])
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
        for key in aggregate:
            aggregate[key] += current["stats"][key]
        all_tensors.extend(current["tensor_observations"])
        runs[split] = {
            "manifest": {"path": current["manifest_path"].relative_to(REPOSITORY_ROOT).as_posix(),
                         "sha256": current["manifest_sha"]},
            "report": {"path": current["report_path"].resolve().relative_to(REPOSITORY_ROOT.resolve()).as_posix(),
                       "sha256": family_ocr._sha256_bytes(current["report_bytes"])},
            "source_count": len(current["images"]), "truth_region_count": truth_count,
            "whole_runtime_elapsed_milliseconds": current["runtime_elapsed_milliseconds"],
            **current["stats"],
        }
    if total_truth != EXPECTED_TOTAL_TRUTHS:
        raise EvidenceError("combined truth count differs from the fixed denominator")
    for stage in metrics:
        metrics[stage]["combined"] = original_scorer._metrics(
            combined_images, combined_truths, combined_predictions[stage])
    complete = (
        aggregate["failed_sources"] == 0 and aggregate["failed_panels"] == 0
        and aggregate["completed_panels"] == sum(
            split["panels"] for split in EXPECTED_COUNTS.values())
        and aggregate["raw_contours"] == aggregate["advisory_selected"])
    tensor_summary = {
        "observation_count": len(all_tensors),
        "maximum_masked_tensor_pixels": max(
            (item["masked_tensor_width"] * item["masked_tensor_height"] for item in all_tensors),
            default=0),
        "maximum_unmasked_tensor_pixels": max(
            (item["unmasked_tensor_width"] * item["unmasked_tensor_height"] for item in all_tensors),
            default=0),
        "total_observer_tensor_pixels": sum(
            item["masked_tensor_width"] * item["masked_tensor_height"]
            + item["unmasked_tensor_width"] * item["unmasked_tensor_height"]
            for item in all_tensors),
    }
    result = {
        "schema": OUTPUT_SCHEMA, "status": "diagnostic_only_unapproved",
        "scope": "local-synthetic-train-dev-high-resolution-diagnostic",
        "detector_maximum_side_length": MAXIMUM_SIDE_LENGTH,
        "detector_resolution_protocol_sha256": protocol_sha,
        "model_input": "original",
        "model_input_protocol_sha256": initial_scorer.EXPECTED_ORIGINAL_PROTOCOL_SHA256,
        "ocr_output_geometry": "initial_db_contour",
        "geometry_protocol_sha256": initial_scorer.EXPECTED_PROTOCOL_SHA256,
        "structure_admission": "advisory",
        "admission_protocol_sha256": advisory_scorer.EXPECTED_PROTOCOL_SHA256,
        "synthetic_only": True, "private_data": False, "sealed_data": False,
        "sealed_runs": 0, "optimizer_steps": 0, "production_approval": False,
        "release_eligible": False,
        "protocol": {"path": PROTOCOL_PATH.as_posix(), "sha256": protocol_sha,
                     "hypothesis": protocol["hypothesis"],
                     "isolated_change": protocol["isolated_change"],
                     "acceptance_bar_reference": protocol["acceptance_bar"]},
        "candidate": {"path": candidate_path.resolve().relative_to(REPOSITORY_ROOT.resolve()).as_posix(),
                      **current_candidate},
        "baseline": {"score": {"path": BASELINE_SCORE_PATH.as_posix(),
                                "sha256": family_ocr._sha256_bytes(baseline_score_bytes)},
                     "candidate": {"path": BASELINE_CANDIDATE_PATH.as_posix(), **baseline_candidate},
                     "detection_metrics": baseline_score["detection_metrics"]},
        "execution_manifest": {
            "path": execution_manifest_path.resolve().relative_to(REPOSITORY_ROOT.resolve()).as_posix(),
            "sha256": family_ocr._sha256_bytes(execution_bytes),
            "all_declared_files_verified": True,
            "runtime_assemblies": [{"name": name, "sha256": digest} for name, digest in assemblies],
        },
        "runs": runs,
        "integrity": {
            "candidate_diff_is_resolution_only": True,
            "baseline_score_authenticated_before_truth": True,
            "source_panel_runtime_and_current_1920_sidecars_verified_before_truth": True,
            "current_raw_ids_expanded_geometry_confidence_and_initial_contours_verified": True,
            "every_current_atomic_initial_contour_emitted_exactly_once": complete,
            "full_source_truth_count": total_truth,
            "complete_identity_verification": complete,
            "final_recognition_trace_complete": aggregate["recognition_failures"] == 0,
            **aggregate,
        },
        "detector_tensor_allocation": tensor_summary,
        "evaluator": {
            "path": "tools/GraphReader.SyntheticRuntimeEvidence/score_high_resolution.py",
            "sha256": expected_evaluator, "reviewed_bytes_authenticated_before_truth": True,
            "matching": "existing maximum-cardinality one-to-one IoU matching",
            "intersection_over_union_minimum": family_ocr.MATCH_IOU_MINIMUM,
            "truth_denominator": "all 146 train plus 183 development regions including failed cases",
            "recognition_scored": False, "roles_scored": False,
        },
        "detection_metrics": metrics,
        "truth_environment": truth_environment,
        "limitations": [
            "Synthetic train/dev evidence cannot approve a model, production stage, or release.",
            "Recognition strings and roles are not scored by this protocol.",
            "The 1920 geometry is authenticated from current-run sidecars and is never reconstructed from 960 evidence.",
            "Runtime and tensor allocation are descriptive and are not acceptance gates.",
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
    parser.add_argument("--evaluator-sha256", required=True)
    arguments = parser.parse_args()
    try:
        score(
            arguments.protocol, arguments.candidate, arguments.candidate_sha256,
            arguments.execution_manifest, arguments.execution_manifest_sha256,
            arguments.train_report, arguments.train_report_sha256,
            arguments.dev_report, arguments.dev_report_sha256,
            arguments.output, evaluator_sha256=arguments.evaluator_sha256)
    except (EvidenceError, OSError, ValueError) as exception:
        print(f"ERROR: {exception}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
