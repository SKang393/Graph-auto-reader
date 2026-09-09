# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Score the authenticated initial-DB-contour OCR output experiment.

The evaluator authenticates the candidate, execution snapshot, train/dev
reports, frozen original-input baseline, and the earlier atomic DB contour
sidecars before it regenerates synthetic truth. The initial contour is accepted
only when its deterministic output identity and polygon trace to the exact raw
expanded detector region selected by the unchanged consensus pairing.
"""

from __future__ import annotations

import argparse
from copy import deepcopy
from hashlib import sha256
from importlib.metadata import version as package_version
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

import score_db_geometry_observation as db_geometry
import score_family_ocr as family_ocr
import score_original_model_input as original_scorer
from ml.ocr.component_region_detector_v6.dataset import Component
from ml.synthetic.fonts import FontResolver


PROTOCOL_PATH = Path("ml/ocr/official_bakeoff/initial_contour_output_dev_protocol.json")
EXPECTED_PROTOCOL_SHA256 = "8e2305ac8632e0bbf675bd36cc25129ba12285cd772cded326560624915abdfb"
ORIGINAL_PROTOCOL_PATH = Path("ml/ocr/official_bakeoff/original_model_input_dev_protocol.json")
EXPECTED_ORIGINAL_PROTOCOL_SHA256 = "ecba1f8ca3c1625c0b47deb6847a7fe9ea9d90e595b7ae8fbd582b39d9c10706"
ORIGINAL_CANDIDATE_PATH = Path(
    "artifacts/synthetic-runtime-evidence/original-model-input-source-c96/candidate.json")
EXPECTED_ORIGINAL_CANDIDATE_SHA256 = "b2d09b04e83b8ebe8a8eaed4951487874a5129cbcab0c24c80a3551079a45833"
ORIGINAL_EXECUTION_PATH = Path(
    "artifacts/synthetic-runtime-evidence/original-model-input-source-c96-executable-v1/execution-files.json")
EXPECTED_ORIGINAL_EXECUTION_SHA256 = "75985af76e6675a48a33eea256487cdcff6e8e9224112dd11084267e725a766a"
ORIGINAL_SCORE_PATH = Path("artifacts/synthetic-runtime-evidence/original-model-input-score-run1.json")
EXPECTED_ORIGINAL_SCORE_SHA256 = "5071f226f99655785e4e706266fa7764aba7bc4dd465831aabc464421eb96ce7"
ORIGINAL_REPORTS = {
    "train": (
        Path("artifacts/synthetic-runtime-evidence/train393-original-model-input-source-c96-run1/report.json"),
        "3170337076d74d16b0c2f59e073eb50e4e8fc0347218ba0e68219cd5f171c20d",
    ),
    "validation": (
        Path("artifacts/synthetic-runtime-evidence/dev393-original-model-input-source-c96-run1/report.json"),
        "273ff697a2440e8c60ed664c58283ba7516a84187e6ba91367c34c790026ce57",
    ),
}
DB_PROTOCOL_PATH = Path("ml/ocr/official_bakeoff/db_geometry_diagnostic_protocol.json")
DB_EXECUTION_PATH = Path(
    "artifacts/synthetic-runtime-evidence/db-geometry-source-c96-executable-v1/execution-files.json")
EXPECTED_DB_EXECUTION_SHA256 = "1239f7dabc5d9d8cebc8d2a5d7b0948aa33f7d39033d2b93f26e1e98773bc5c6"
DB_REPORTS = {
    "train": (
        Path("artifacts/synthetic-runtime-evidence/train393-db-geometry-source-c96-run1/report.json"),
        "679effc25d675879a47a742685d0d891f21bf177246251d32ec9ea88e9dff207",
    ),
    "validation": (
        Path("artifacts/synthetic-runtime-evidence/dev393-db-geometry-source-c96-run1/report.json"),
        "a0c07332ce44baa6b1513c865af17404207edf6c19332bf4014cd1bfaf81483f",
    ),
}
COUNTERFACTUAL_HELPER_PATH = Path(
    "artifacts/synthetic-runtime-evidence/initial-output-counterfactual/diagnose.py")
EXPECTED_COUNTERFACTUAL_HELPER_SHA256 = "dd57083a0ca53e7685d75da4685032ab2d10e340dc604cdf8748b87851a5d3eb"
COUNTERFACTUAL_REPORT_PATH = Path(
    "artifacts/synthetic-runtime-evidence/initial-output-counterfactual/report.json")
EXPECTED_COUNTERFACTUAL_REPORT_SHA256 = "cf47aa87d5784023d7ad5b2e41d6c88f0479a3d15bcb87d6a689f22944e9f1a7"
SOURCE_BINDINGS = {
    "tools/GraphReader.SyntheticRuntimeEvidence/score_original_model_input.py":
        "a36245ec82dcba0bc4f1a29ae04fc4c1417bf2d9ad113ff5b30d9ac93385b756",
    "tools/GraphReader.SyntheticRuntimeEvidence/score_db_geometry_observation.py":
        "12569c6e68581d57959cf226eb91c8d1cc429ea5cf9cc111727bc9e52e9bea10",
    "tools/GraphReader.SyntheticRuntimeEvidence/score_family_ocr.py":
        "431392d074e985f953a1444982076466f461d3445c018a5c307f174ebc0a959e",
    "ml/synthetic/dataset.py":
        "a1a97394c2937ebb00b01e941466188876f5b5478e012dc41503fb38696d991b",
    "ml/synthetic/templates.py":
        "f59733c5f0589bd1b9143d3c9ae83015b4c53ab593de229d56e46d9f11ec2f1c",
    "ml/synthetic/renderer.py":
        "6ddba4f5392a078d6c4d0108ca3a48c31f8d3692cdd26fbdbd466849ae5bef1b",
    "ml/synthetic/fonts.py":
        "ca44e2f105648a17f2d1f4c977d8fca64c6f16a37fb67133beadb711a37a8b0d",
    "ml/synthetic/schema.py":
        "08360ded42755a0fcf688d4fd7f16320d65909ab90275ac4f3cb7ec14784af98",
    "ml/synthetic/scene.schema.json":
        "fc72d6ce4e37dd2893e5fe56d087b0496008a904f11ebc4a5c525a4a27843e0f",
    "ml/synthetic/io.py":
        "0f3b81f55d8b0ca89cd8127edf351dbe23948e3610c71c845112bc9084e3b9c2",
    "ml/markers/center/mask_preserving_v24/family_scenes.py":
        "0872091244ebe4a2e87192c74b0720a911255816ced4b4fb55df811305a92686",
    "ml/ocr/component_region_detector_v6/dataset.py":
        "cfc1760cfe0f5231d34960100bed3b24bbd54bab6ab69919c3eae43fddc22f59",
    "ml/ocr/real_range_proposal_v34/pipeline.py":
        "7cfc997de0ceb39edfbc4f50c0ea5f07fb058f8eb65ceb21745ef099e291afa5",
    "ml/ocr/component_context_detector_v7/dataset.py":
        "96bebccedc58404e369a1ac3ef6fe3d4d8baa657872543f8958ed73e902d595f",
    "ml/synthetic/requirements.txt":
        "e7566b9f1fd194d241e4c758976cc2f3b2062b3a9481a7383c05949c5ed6e078",
}
PACKAGE_BINDINGS = {"Pillow": "12.3.0", "jsonschema": "4.26.0"}
FONT_BINDINGS = {
    "sans": ("arial.ttf", "b3658eadae55e682b5f69eb64c439c1ecc8f196c0bb8d4756d145d13bc86476a"),
    "serif": ("times.ttf", "931c5de5c70401d9324d5014c123802b4fb753000360ceb2f56c589403cd58c5"),
    "mono": ("consola.ttf", "cf00b507b3286870cc5064ebd0633c303f70b491a4af25eec2d32df413db0179"),
}
INITIAL_COMPOSITION = "graph-structure-consensus-initial-db-contour-original-model-input-v1"
OUTPUT_SCHEMA = "graphreader.synthetic-initial-contour-output-score.v2"
PROTOCOL_KEYS = {
    "evidence_policy", "hypothesis", "isolated_change", "split_identities",
    "metric", "acceptance_bar", "budget",
}
IDENTITY_KEYS = {
    "baseline_candidate_path", "baseline_candidate_sha256",
    "original_input_protocol_path", "original_input_protocol_sha256",
    "train_manifest_path", "train_manifest_sha256", "dev_manifest_path",
    "dev_manifest_sha256", "train_source_count", "train_panel_count",
    "train_truth_count", "dev_source_count", "dev_panel_count", "dev_truth_count",
}
EXPECTED_COUNTS = {
    "train": {"sources": 4, "panels": 4, "truths": 146},
    "validation": {"sources": 3, "panels": 9, "truths": 183},
}
EXPECTED_TOTAL_TRUTHS = 329
EXPECTED_BASELINE_SELECTED = 205

EvidenceError = family_ocr.EvidenceError


def _repository_path(relative: Path, label: str) -> Path:
    if relative.is_absolute():
        raise EvidenceError(f"{label} path must be repository-relative")
    path = (REPOSITORY_ROOT / relative).resolve()
    root = REPOSITORY_ROOT.resolve()
    if path == root or root not in path.parents:
        raise EvidenceError(f"{label} path escaped the repository")
    return path


def _exact_file(relative: Path, expected_sha256: str, label: str) -> tuple[Path, bytes]:
    path = _repository_path(relative, label)
    payload = path.read_bytes()
    expected = family_ocr._require_sha256(expected_sha256, f"{label} SHA-256")
    if family_ocr._sha256_bytes(payload) != expected:
        raise EvidenceError(f"{label} bytes differ from the fixed identity")
    return path, payload


def _load_exact_object(relative: Path, expected_sha256: str, label: str) -> dict[str, Any]:
    path, payload = _exact_file(relative, expected_sha256, label)
    try:
        value = json.loads(payload)
    except json.JSONDecodeError as exception:
        raise EvidenceError(f"{label} is invalid JSON: {exception}") from exception
    if not isinstance(value, dict):
        raise EvidenceError(f"{label} must be an object")
    return value


def _validate_truth_environment() -> dict[str, Any]:
    """Bind truth and matching dependencies before any synthetic regeneration.

    Existing generator and evaluator identities match prior family evidence and
    the reserve generator snapshot. The v7 import bridge and requirements file
    are additionally pinned for this model-free v2 repair. Fonts remain local.
    """
    sources = []
    for relative, expected in SOURCE_BINDINGS.items():
        _exact_file(Path(relative), expected, f"evaluator source {relative}")
        sources.append({"path": relative, "sha256": expected})
    packages = []
    for name, expected in PACKAGE_BINDINGS.items():
        if package_version(name) != expected:
            raise EvidenceError(f"truth package {name} differs from its fixed version")
        packages.append({"name": name, "version": expected})
    resolver = FontResolver()
    fonts = []
    for alias, (filename, expected) in FONT_BINDINGS.items():
        resolved = resolver.resolve(alias, size_px=14)
        if (resolved.path.name.lower() != filename
                or sha256(resolved.path.read_bytes()).hexdigest() != expected):
            raise EvidenceError(f"truth font {alias} differs from its fixed identity")
        fonts.append({"alias": alias, "filename": filename, "size_px": 14,
                      "sha256": expected, "bundled": False})
    return {"source_dependencies": sources, "packages": packages, "fonts": fonts}


def _protocol_file(path_value: Any, expected: Path, label: str) -> Path:
    if not isinstance(path_value, str) or Path(path_value).as_posix() != expected.as_posix():
        raise EvidenceError(f"{label} does not use its fixed repository path")
    return _repository_path(expected, label)


def _load_protocol(protocol_path: Path) -> tuple[dict[str, Any], bytes, dict[str, Any]]:
    expected_path = _repository_path(PROTOCOL_PATH, "initial-contour protocol")
    if protocol_path.resolve() != expected_path:
        raise EvidenceError("initial-contour scoring requires the reviewed repository protocol path")
    protocol, payload = family_ocr._load_object(expected_path, "initial-contour protocol")
    if family_ocr._sha256_bytes(payload) != EXPECTED_PROTOCOL_SHA256:
        raise EvidenceError("initial-contour protocol bytes differ from the reviewed protocol")
    family_ocr._require_exact_keys(protocol, PROTOCOL_KEYS, "initial-contour protocol")
    if (
        protocol.get("evidence_policy") != "ml/policy/evidence-policy.json"
        or not all(isinstance(protocol.get(key), str) and protocol[key].strip()
                   for key in ("hypothesis", "isolated_change", "metric", "acceptance_bar"))
    ):
        raise EvidenceError("initial-contour protocol metadata is invalid")
    budget = db_geometry._require_mapping(protocol.get("budget"), "initial-contour budget")
    family_ocr._require_exact_keys(
        budget,
        {"synthetic_train_dev_runs", "optimizer_steps", "private_reads", "sealed_runs",
         "production_approval", "release_eligible"},
        "initial-contour budget",
    )
    if budget != {
        "synthetic_train_dev_runs": "unlimited", "optimizer_steps": 0,
        "private_reads": 0, "sealed_runs": 0, "production_approval": False,
        "release_eligible": False,
    }:
        raise EvidenceError("initial-contour protocol cannot authorize training, private, sealed, production, or release use")
    identities = db_geometry._require_mapping(
        protocol.get("split_identities"), "initial-contour split identities")
    family_ocr._require_exact_keys(identities, IDENTITY_KEYS, "initial-contour split identities")
    if (
        identities["baseline_candidate_path"] != ORIGINAL_CANDIDATE_PATH.as_posix()
        or identities["baseline_candidate_sha256"] != EXPECTED_ORIGINAL_CANDIDATE_SHA256
        or identities["original_input_protocol_path"] != ORIGINAL_PROTOCOL_PATH.as_posix()
        or identities["original_input_protocol_sha256"] != EXPECTED_ORIGINAL_PROTOCOL_SHA256
    ):
        raise EvidenceError("initial-contour protocol does not bind the fixed original-input baseline")
    for split, prefix in (("train", "train"), ("validation", "dev")):
        expected = EXPECTED_COUNTS[split]
        if any(identities[f"{prefix}_{name}_count"] != expected[name + "s"]
               for name in ("source", "panel", "truth")):
            raise EvidenceError(f"initial-contour protocol {split} counts differ from the fixed denominator")
    return protocol, payload, dict(identities)


def _validate_isolated_candidate(
    baseline: Mapping[str, Any], candidate: Mapping[str, Any], protocol_sha256: str,
) -> None:
    if candidate.get("ocr_output_geometry") != "initial_db_contour":
        raise EvidenceError("experimental candidate must select initial DB contour output geometry")
    if candidate.get("initial_contour_protocol") != {
        "path": PROTOCOL_PATH.as_posix(), "sha256": protocol_sha256,
    }:
        raise EvidenceError("experimental candidate does not bind the reviewed geometry protocol")
    normalized = deepcopy(dict(candidate))
    normalized.pop("ocr_output_geometry", None)
    normalized.pop("initial_contour_protocol", None)
    if normalized != baseline:
        raise EvidenceError("experimental candidate changes more than the reviewed output-geometry fields")


def _write_7bit_length(value: int) -> bytes:
    if value < 0:
        raise ValueError("length must be nonnegative")
    result = bytearray()
    while value >= 0x80:
        result.append((value | 0x80) & 0xFF)
        value >>= 7
    result.append(value)
    return bytes(result)


def _initial_output_region_id(
    model_region_id: str, component_region_id: str, polygon: Mapping[str, Any],
) -> str:
    try:
        UUID(model_region_id)
        UUID(component_region_id)
    except ValueError as exception:
        raise EvidenceError("selected model and component region IDs must be UUIDs") from exception
    points = db_geometry._require_list(polygon.get("points"), "initial polygon points")
    if len(points) < 3:
        raise EvidenceError("initial polygon must have at least three points")
    material = bytearray()
    for value in (INITIAL_COMPOSITION, model_region_id, component_region_id):
        encoded = value.encode("utf-8")
        material.extend(_write_7bit_length(len(encoded)))
        material.extend(encoded)
    material.extend(struct.pack("<i", len(points)))
    for index, raw in enumerate(points):
        point = db_geometry._require_mapping(raw, f"initial polygon point {index}")
        x = family_ocr._require_number(point.get("x"), f"initial polygon point {index} x")
        y = family_ocr._require_number(point.get("y"), f"initial polygon point {index} y")
        if not math.isfinite(x) or not math.isfinite(y):
            raise EvidenceError("initial polygon points must be finite")
        material.extend(struct.pack("<d", x))
        material.extend(struct.pack("<d", y))
    return str(UUID(bytes_le=sha256(material).digest()[:16]))


def _bounds(region: Mapping[str, Any], label: str) -> tuple[float, float, float, float]:
    polygon = db_geometry._require_mapping(region.get("polygon"), f"{label} polygon")
    bounds = db_geometry._require_mapping(polygon.get("bounds"), f"{label} bounds")
    result = tuple(
        family_ocr._require_number(bounds.get(field), f"{label} {field}")
        for field in ("left", "top", "right", "bottom")
    )
    if result[2] <= result[0] or result[3] <= result[1]:
        raise EvidenceError(f"{label} bounds are not positive")
    return result  # type: ignore[return-value]


def _overlap_coefficient(left: Mapping[str, Any], right: Mapping[str, Any]) -> float:
    l1, t1, r1, b1 = _bounds(left, "model region")
    l2, t2, r2, b2 = _bounds(right, "component region")
    intersection = max(0.0, min(r1, r2) - max(l1, l2)) * max(0.0, min(b1, b2) - max(t1, t2))
    return intersection / min((r1 - l1) * (b1 - t1), (r2 - l2) * (b2 - t2))


def _consensus_pairs(
    model_regions: Sequence[Mapping[str, Any]], component_regions: Sequence[Mapping[str, Any]],
) -> list[tuple[Mapping[str, Any], Mapping[str, Any]]]:
    eligible: list[tuple[int, Mapping[str, Any]]] = []
    for index, component in enumerate(component_regions):
        evidence = db_geometry._require_mapping(component.get("evidence"), "component evidence")
        likelihood = family_ocr._require_number(evidence.get("text_likelihood"), "component text likelihood")
        if evidence.get("likely_graph_structure") is False and likelihood >= 0.45:
            eligible.append((index, component))
    matches: list[tuple[int, int, float, Mapping[str, Any], Mapping[str, Any]]] = []
    for model_index, model in enumerate(model_regions):
        for component_index, component in eligible:
            overlap = _overlap_coefficient(model, component)
            if overlap >= 0.50:
                matches.append((model_index, component_index, overlap, model, component))
    matches.sort(key=lambda item: (
        -family_ocr._require_number(item[3].get("detection_confidence"), "model confidence"),
        -item[2],
        -family_ocr._require_number(
            db_geometry._require_mapping(item[4].get("evidence"), "component evidence").get("text_likelihood"),
            "component text likelihood"),
        str(item[3].get("region_id")), str(item[4].get("region_id")),
    ))
    used_models: set[int] = set()
    used_components: set[int] = set()
    selected: list[tuple[Mapping[str, Any], Mapping[str, Any]]] = []
    for model_index, component_index, _, model, component in matches:
        if model_index in used_models or component_index in used_components:
            continue
        used_models.add(model_index)
        used_components.add(component_index)
        selected.append((model, component))
    return selected


def _sidecar_value(
    panel: Mapping[str, Any], source: Mapping[str, Any], report_path: Path,
) -> Mapping[str, Any]:
    diagnostic = db_geometry._require_mapping(
        panel.get("ocr_proposal_diagnostic"), "DB OCR proposal diagnostic")
    descriptor = db_geometry._require_mapping(
        diagnostic.get("db_geometry_diagnostic_sidecar"), "DB geometry sidecar descriptor")
    path = report_path.parent / str(source["image_sha256"]) / str(panel["panel_id"]) / str(descriptor["file"])
    value, payload = family_ocr._load_object(path, "DB geometry sidecar")
    if family_ocr._sha256_bytes(payload) != descriptor.get("sha256"):
        raise EvidenceError("DB geometry sidecar changed after complete validation")
    return value


def _unmasked_contours(sidecar: Mapping[str, Any]) -> dict[str, Mapping[str, Any]]:
    invocations = db_geometry._require_list(sidecar.get("invocations"), "DB geometry invocations")
    if len(invocations) != 2 or invocations[1].get("kind") != "unmasked":
        raise EvidenceError("DB geometry sidecar lacks its fixed unmasked invocation")
    observation = db_geometry._require_mapping(invocations[1].get("observation"), "unmasked DB observation")
    contours = db_geometry._require_list(observation.get("accepted_contours"), "unmasked accepted contours")
    result: dict[str, Mapping[str, Any]] = {}
    for raw in contours:
        contour = db_geometry._require_mapping(raw, "unmasked accepted contour")
        identifier = str(contour.get("returned_region_id"))
        if not identifier or identifier in result:
            raise EvidenceError("unmasked accepted-contour identities must be nonempty and unique")
        result[identifier] = contour
    return result


def _expected_adapter_id(original_adapter: Any) -> str:
    if not isinstance(original_adapter, str):
        raise EvidenceError("original report OCR adapter ID is missing")
    token = "graph-structure-consensus-original-model-input-v1"
    if original_adapter.count(token) != 1:
        raise EvidenceError("original report OCR adapter ID has an unexpected composition")
    return original_adapter.replace(token, INITIAL_COMPOSITION)


def _validate_report_geometry(report: Mapping[str, Any], original_report: Mapping[str, Any]) -> None:
    if family_ocr._require_sha256(
        report.get("geometry_protocol_sha256"), "report geometry protocol SHA-256") != EXPECTED_PROTOCOL_SHA256:
        raise EvidenceError("initial-contour report does not bind the reviewed geometry protocol")
    if report.get("ocr_adapter_id") != _expected_adapter_id(original_report.get("ocr_adapter_id")):
        raise EvidenceError("initial-contour report OCR composition identity is invalid")


def _validate_panel_provenance(original: Mapping[str, Any], current: Mapping[str, Any]) -> None:
    fields = (
        "image_sha256", "width", "height", "source_image_sha256", "source_width",
        "source_height", "crop", "requested_crop", "source_to_panel_matrix",
        "panel_to_source_matrix", "panel_png", "source_gray", "detector_input_sha256",
        "detector_bgr_sha256", "pre_ocr_diagnostic",
    )
    if any(original.get(field) != current.get(field) for field in fields):
        raise EvidenceError("initial and original panels differ in source, crop, or detector input")


def _validate_completed_panel(
    original_panel: Mapping[str, Any], current_panel: Mapping[str, Any],
    db_panel: Mapping[str, Any], db_source: Mapping[str, Any], db_report_path: Path,
    candidate: Mapping[str, Any],
) -> tuple[tuple[Component, ...], tuple[Component, ...], dict[str, Any]]:
    _validate_panel_provenance(original_panel, current_panel)
    original_diagnostic = db_geometry._require_mapping(
        original_panel.get("ocr_proposal_diagnostic"), "original OCR proposal diagnostic")
    current_diagnostic = db_geometry._require_mapping(
        current_panel.get("ocr_proposal_diagnostic"), "initial OCR proposal diagnostic")
    unchanged_fields = (
        "model_regions", "unmasked_model_regions", "component_regions",
        "detector_input_sha256", "unmasked_input_sha256",
    )
    if any(original_diagnostic.get(field) != current_diagnostic.get(field) for field in unchanged_fields):
        raise EvidenceError("initial-contour run changed raw learned or component evidence")
    raw_regions = [
        db_geometry._require_mapping(value, "unmasked raw region")
        for value in db_geometry._require_list(
            current_diagnostic.get("unmasked_model_regions"), "unmasked raw regions")
    ]
    raw_by_id = {str(region.get("region_id")): region for region in raw_regions}
    if len(raw_by_id) != len(raw_regions) or not all(raw_by_id):
        raise EvidenceError("unmasked raw region IDs must be nonempty and unique")
    components = [
        db_geometry._require_mapping(value, "component region")
        for value in db_geometry._require_list(current_diagnostic.get("component_regions"), "component regions")
    ]
    pairs = _consensus_pairs(raw_regions, components)
    selected_raw_ids = [str(model.get("region_id")) for model, _ in pairs]

    sidecar = _sidecar_value(db_panel, db_source, db_report_path)
    contours = _unmasked_contours(sidecar)
    if set(contours) != set(raw_by_id):
        raise EvidenceError("current original-input raw IDs differ from authenticated DB contours")
    for identifier, raw in raw_by_id.items():
        contour = contours[identifier]
        if (
            contour.get("expanded_polygon") != raw.get("polygon")
            or contour.get("detection_confidence") != raw.get("detection_confidence")
        ):
            raise EvidenceError("current raw expanded geometry or confidence differs from the authenticated DB contour")

    original_ocr = db_geometry._require_mapping(original_panel.get("ocr"), "original OCR output")
    original_final = [
        db_geometry._require_mapping(value, "original final OCR region")
        for value in db_geometry._require_list(original_ocr.get("regions"), "original final OCR regions")
    ]
    original_final_by_id = {str(region.get("region_id")): region for region in original_final}
    if len(original_final_by_id) != len(original_final) or set(original_final_by_id) != set(selected_raw_ids):
        raise EvidenceError("replayed consensus-selected expanded IDs differ from the original run")

    current_ocr = db_geometry._require_mapping(current_panel.get("ocr"), "initial OCR output")
    current_final = [
        db_geometry._require_mapping(value, "initial final OCR region")
        for value in db_geometry._require_list(current_ocr.get("regions"), "initial final OCR regions")
    ]
    failures = db_geometry._require_list(current_ocr.get("region_failures"), "initial OCR region failures")
    cache = db_geometry._require_mapping(current_ocr.get("cache"), "initial OCR cache")
    if failures:
        raise EvidenceError("completed initial-contour panel has OCR region failures")
    if family_ocr._require_int(cache.get("crop_count"), "initial OCR crop count", minimum=0) != len(current_final):
        raise EvidenceError("initial OCR crop count differs from final regions")
    if len(current_final) != len(pairs):
        raise EvidenceError("initial OCR final count differs from unchanged consensus selection")
    masked_sha = family_ocr._require_sha256(
        current_diagnostic.get("detector_input_sha256"), "axis-masked detector input SHA-256")
    original_sha = family_ocr._require_sha256(
        current_diagnostic.get("unmasked_input_sha256"), "original detector input SHA-256")
    original_scorer._runtime_models(
        current_panel, candidate, original_sha, masked_sha, "original", len(current_final) > 0)

    width_ratios: list[float] = []
    height_ratios: list[float] = []
    area_ratios: list[float] = []
    text_changes = alternative_changes = role_changes = confidence_changes = 0
    expected: dict[str, tuple[Mapping[str, Any], Mapping[str, Any], Mapping[str, Any]]] = {}
    for index, (raw, component) in enumerate(pairs):
        raw_id = str(raw.get("region_id"))
        component_id = str(component.get("region_id"))
        initial_polygon = db_geometry._require_mapping(
            contours[raw_id].get("initial_polygon"), f"selected initial polygon {index}")
        expected_id = _initial_output_region_id(raw_id, component_id, initial_polygon)
        if expected_id in expected:
            raise EvidenceError("deterministic initial output identities are not unique")
        expected[expected_id] = (raw, component, initial_polygon)
    current_final_by_id = {str(region.get("region_id")): region for region in current_final}
    if len(current_final_by_id) != len(current_final) or set(current_final_by_id) != set(expected):
        raise EvidenceError("final initial region IDs differ from deterministic atomic contour identities")
    for expected_id, (raw, _, initial_polygon) in expected.items():
        final = current_final_by_id[expected_id]
        raw_id = str(raw.get("region_id"))
        baseline_region = original_final_by_id[raw_id]
        if final.get("polygon") != initial_polygon:
            raise EvidenceError("final initial region does not match its deterministic atomic contour identity")
        expanded_bounds = _bounds(raw, "selected expanded region")
        initial_bounds = _bounds(final, "selected initial region")
        ew, eh = expanded_bounds[2] - expanded_bounds[0], expanded_bounds[3] - expanded_bounds[1]
        iw, ih = initial_bounds[2] - initial_bounds[0], initial_bounds[3] - initial_bounds[1]
        width_ratios.append(iw / ew)
        height_ratios.append(ih / eh)
        area_ratios.append((iw * ih) / (ew * eh))
        text_changes += int(final.get("text") != baseline_region.get("text"))
        alternative_changes += int(final.get("alternatives") != baseline_region.get("alternatives"))
        role_changes += int(final.get("role") != baseline_region.get("role"))
        confidence_changes += int(final.get("confidence") != baseline_region.get("confidence"))

    return (
        original_scorer._region_components(raw_regions, current_panel, "raw expanded regions"),
        original_scorer._final_components(current_panel),
        {
            "selected": len(current_final), "text_changes": text_changes,
            "alternative_changes": alternative_changes, "role_changes": role_changes,
            "confidence_changes": confidence_changes, "width_ratios": width_ratios,
            "height_ratios": height_ratios, "area_ratios": area_ratios,
        },
    )


def _summary(values: Sequence[float]) -> dict[str, float] | None:
    if not values:
        return None
    if any(not math.isfinite(value) for value in values):
        raise EvidenceError("geometry ratios must be finite")
    return {"minimum": min(values), "median": statistics.median(values), "maximum": max(values)}


def _validate_reference_score(value: Mapping[str, Any]) -> None:
    raw = value.get("detection_metrics", {}).get("original_model_input", {}).get("raw_expanded", {}).get("combined")
    final = value.get("detection_metrics", {}).get("original_model_input", {}).get("final_ocr_regions", {}).get("combined")
    if (
        value.get("schema") != original_scorer.OUTPUT_SCHEMA
        or value.get("model_input") != "original"
        or value.get("integrity", {}).get("full_source_truth_count") != EXPECTED_TOTAL_TRUTHS
        or not isinstance(raw, dict) or not isinstance(final, dict)
        or {key: raw.get(key) for key in ("true_positives", "false_positives", "false_negatives")}
        != {"true_positives": 106, "false_positives": 240, "false_negatives": 223}
        or final.get("predicted_region_count") != EXPECTED_BASELINE_SELECTED
        or {key: final.get(key) for key in ("true_positives", "false_positives", "false_negatives")}
        != {"true_positives": 79, "false_positives": 126, "false_negatives": 250}
    ):
        raise EvidenceError("authenticated original-input score differs from its fixed observations")


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
    output_path = family_ocr._require_artifact_path(
        output_path, REPOSITORY_ROOT, "initial-contour score output")
    if output_path.exists():
        raise EvidenceError("refusing to replace an existing initial-contour score output")
    expected_evaluator = family_ocr._require_sha256(evaluator_sha256, "reviewed evaluator SHA-256")
    if sha256(Path(__file__).read_bytes()).hexdigest() != expected_evaluator:
        raise EvidenceError("current evaluator bytes differ from the reviewed identity")
    protocol, protocol_bytes, identities = _load_protocol(protocol_path)
    protocol_sha = family_ocr._sha256_bytes(protocol_bytes)

    truth_environment = _validate_truth_environment()
    _exact_file(COUNTERFACTUAL_HELPER_PATH, EXPECTED_COUNTERFACTUAL_HELPER_SHA256, "counterfactual helper")
    counterfactual = _load_exact_object(
        COUNTERFACTUAL_REPORT_PATH, EXPECTED_COUNTERFACTUAL_REPORT_SHA256, "counterfactual report")
    if (
        counterfactual.get("integrity", {}).get("full_source_truth_denominator") != EXPECTED_TOTAL_TRUTHS
        or counterfactual.get("integrity", {}).get("selected_region_count") != EXPECTED_BASELINE_SELECTED
    ):
        raise EvidenceError("counterfactual reference does not contain the fixed denominator and selection")
    original_score = _load_exact_object(
        ORIGINAL_SCORE_PATH, EXPECTED_ORIGINAL_SCORE_SHA256, "original-input score")
    _validate_reference_score(original_score)

    _, original_protocol_bytes, original_bound = original_scorer._load_protocol(
        _repository_path(ORIGINAL_PROTOCOL_PATH, "original-input protocol"))
    original_protocol_sha = family_ocr._sha256_bytes(original_protocol_bytes)
    if original_protocol_sha != EXPECTED_ORIGINAL_PROTOCOL_SHA256:
        raise EvidenceError("original-input protocol identity changed")
    original_raw, original_candidate = original_scorer._validate_complete_candidate(
        _repository_path(ORIGINAL_CANDIDATE_PATH, "original-input candidate"),
        EXPECTED_ORIGINAL_CANDIDATE_SHA256,
    )
    axis_raw, axis_candidate = original_scorer._validate_complete_candidate(
        *original_bound["baseline_candidate"])
    original_scorer._validate_isolated_candidate(
        axis_raw, original_raw, original_protocol_sha)
    current_raw, current_candidate = original_scorer._validate_complete_candidate(
        candidate_path, family_ocr._require_sha256(candidate_sha256, "candidate SHA-256"))
    _validate_isolated_candidate(original_raw, current_raw, protocol_sha)
    original_execution_bytes, original_assemblies = db_geometry._validate_execution_manifest(
        _repository_path(ORIGINAL_EXECUTION_PATH, "original-input execution manifest"),
        EXPECTED_ORIGINAL_EXECUTION_SHA256,
    )
    current_execution_bytes, current_assemblies = db_geometry._validate_execution_manifest(
        execution_manifest_path, execution_manifest_sha256)

    _, db_protocol_bytes, db_bound = db_geometry._load_protocol(
        _repository_path(DB_PROTOCOL_PATH, "DB geometry protocol"))
    db_protocol_sha = family_ocr._sha256_bytes(db_protocol_bytes)
    db_candidate = db_geometry._validate_candidate(*db_bound["runtime_candidate"])
    if db_candidate["sha256"] != axis_candidate["sha256"]:
        raise EvidenceError("DB contour candidate differs from the original-input baseline candidate")
    db_execution_bytes, db_assemblies = db_geometry._validate_execution_manifest(
        _repository_path(DB_EXECUTION_PATH, "DB execution manifest"), EXPECTED_DB_EXECUTION_SHA256)

    requested = {
        "train": (train_report_path, train_report_sha256),
        "validation": (dev_report_path, dev_report_sha256),
    }
    manifest_keys = {"train": "train_manifest", "validation": "dev_manifest"}
    baseline_keys = {"train": "baseline_train_report", "validation": "baseline_dev_report"}
    validated: dict[str, Any] = {}
    all_sources: set[str] = set()
    all_panels: set[str] = set()
    for split in ("train", "validation"):
        manifest_path, manifest_sha = original_bound[manifest_keys[split]]
        configured_manifest = _protocol_file(
            identities[("train" if split == "train" else "dev") + "_manifest_path"],
            Path(manifest_path.relative_to(REPOSITORY_ROOT)), f"{split} manifest")
        configured_sha = identities[("train" if split == "train" else "dev") + "_manifest_sha256"]
        if configured_manifest != manifest_path or configured_sha != manifest_sha:
            raise EvidenceError(f"initial-contour and original protocols bind different {split} manifests")

        original_report_path, original_report_sha = ORIGINAL_REPORTS[split]
        original_run = original_scorer._validate_report(
            split, manifest_path, manifest_sha,
            _repository_path(original_report_path, f"{split} original report"), original_report_sha,
            original_candidate, "original", original_protocol_sha, original_assemblies)
        current_run = original_scorer._validate_report(
            split, manifest_path, manifest_sha, requested[split][0], requested[split][1],
            current_candidate, "original", original_protocol_sha, current_assemblies)
        _validate_report_geometry(current_run[2], original_run[2])

        db_report_relative, db_report_sha = DB_REPORTS[split]
        db_report_path, db_report_bytes = _exact_file(
            db_report_relative, db_report_sha, f"{split} DB geometry report")
        db_run = db_geometry._validate_run(
            split, manifest_path, db_report_path, manifest_sha, db_protocol_sha,
            db_candidate, db_assemblies)
        if family_ocr._sha256_bytes(db_run[3]) != db_report_sha:
            raise EvidenceError(f"{split} DB geometry report changed during validation")

        images, original_cases = original_run[0], original_run[1]
        current_images, current_cases = current_run[0], current_run[1]
        db_images, db_cases = db_run[0], db_run[1]
        if images != current_images or images != db_images or set(original_cases) != set(current_cases) or set(original_cases) != set(db_cases):
            raise EvidenceError(f"{split} reports do not cover identical source identities")
        if len(images) != EXPECTED_COUNTS[split]["sources"]:
            raise EvidenceError(f"{split} source count differs from the reviewed protocol")
        source_ids = set(original_cases)
        if source_ids.intersection(all_sources):
            raise EvidenceError("train and validation reuse a source identity")
        all_sources.update(source_ids)

        predictions = {"raw_expanded": {}, "final_initial": {}}
        stats = {
            "failed_sources": 0, "failed_panels": 0, "completed_panels": 0,
            "selected": 0, "unavailable_baseline_selected": 0,
            "text_changes": 0, "alternative_changes": 0, "role_changes": 0,
            "confidence_changes": 0, "width_ratios": [], "height_ratios": [], "area_ratios": [],
        }
        original_panel_total = 0
        for image in images:
            source_sha = str(image["image_sha256"])
            predictions["raw_expanded"][source_sha] = []
            predictions["final_initial"][source_sha] = []
            original_source = original_cases[source_sha]
            current_source = current_cases[source_sha]
            db_source = db_cases[source_sha]
            original_panels = original_scorer._panel_map(original_source, "original source")
            db_panels = original_scorer._panel_map(db_source, "DB source")
            if set(original_panels) != set(db_panels):
                raise EvidenceError("original and DB reports use different panel crops")
            original_panel_total += len(original_panels)
            if current_source.get("status") == "failed":
                stats["failed_sources"] += 1
                stats["unavailable_baseline_selected"] += sum(
                    len(db_geometry._require_list(panel.get("ocr", {}).get("regions"), "original regions"))
                    for panel in original_panels.values())
                continue
            current_panels = original_scorer._panel_map(current_source, "initial source")
            if set(current_panels) != set(original_panels):
                raise EvidenceError("completed initial source uses different panel crops")
            for crop, original_panel in original_panels.items():
                current_panel = current_panels[crop]
                panel_id = str(current_panel.get("panel_id"))
                if panel_id in all_panels:
                    raise EvidenceError("train and validation reuse a panel identity")
                all_panels.add(panel_id)
                if current_panel.get("status") != "seed-completed":
                    stats["failed_panels"] += 1
                    stats["unavailable_baseline_selected"] += len(
                        db_geometry._require_list(original_panel.get("ocr", {}).get("regions"), "original regions"))
                    continue
                raw_components, final_components, panel_stats = _validate_completed_panel(
                    original_panel, current_panel, db_panels[crop], db_source,
                    db_report_path, current_candidate)
                predictions["raw_expanded"][source_sha].extend(raw_components)
                predictions["final_initial"][source_sha].extend(final_components)
                stats["completed_panels"] += 1
                for name in ("selected", "text_changes", "alternative_changes", "role_changes", "confidence_changes"):
                    stats[name] += panel_stats[name]
                for name in ("width_ratios", "height_ratios", "area_ratios"):
                    stats[name].extend(panel_stats[name])
        if original_panel_total != EXPECTED_COUNTS[split]["panels"]:
            raise EvidenceError(f"{split} panel count differs from the reviewed protocol")
        validated[split] = {
            "manifest_path": manifest_path, "manifest_sha": manifest_sha,
            "images": images, "dataset_seed": original_run[4],
            "original_report_path": _repository_path(original_report_path, f"{split} original report"),
            "original_report_sha": original_report_sha,
            "current_report_path": requested[split][0], "current_report_bytes": current_run[3],
            "db_report_path": db_report_path, "db_report_sha": db_report_sha,
            "predictions": predictions, "stats": stats,
        }

    metrics = {stage: {} for stage in ("raw_expanded", "final_initial")}
    combined_images: list[Mapping[str, Any]] = []
    combined_truths: dict[str, Sequence[Any]] = {}
    combined_predictions = {stage: {} for stage in metrics}
    runs: dict[str, Any] = {}
    total_truth = 0
    aggregate_stats = {
        "failed_sources": 0, "failed_panels": 0, "completed_panels": 0,
        "selected": 0, "unavailable_baseline_selected": 0,
        "text_changes": 0, "alternative_changes": 0, "role_changes": 0,
        "confidence_changes": 0, "width_ratios": [], "height_ratios": [], "area_ratios": [],
    }

    # No renderer truth is created until both splits, all runtime artifacts,
    # every old contour sidecar, and every completed output mapping pass above.
    for split in ("train", "validation"):
        current = validated[split]
        images = current["images"]
        regenerated = family_ocr._regenerate(split, current["dataset_seed"], images)
        truths: dict[str, Sequence[Any]] = {}
        for image in images:
            source_sha = str(image["image_sha256"])
            truths[source_sha] = family_ocr._truth_regions(regenerated[source_sha][1])[0]
        truth_count = sum(len(value) for value in truths.values())
        if truth_count != EXPECTED_COUNTS[split]["truths"]:
            raise EvidenceError(f"{split} truth count differs from the reviewed full denominator")
        total_truth += truth_count
        for stage in metrics:
            metrics[stage][split] = original_scorer._metrics(
                images, truths, current["predictions"][stage])
            combined_predictions[stage].update(current["predictions"][stage])
        combined_images.extend(images)
        combined_truths.update(truths)
        for name in aggregate_stats:
            if isinstance(aggregate_stats[name], list):
                aggregate_stats[name].extend(current["stats"][name])
            else:
                aggregate_stats[name] += current["stats"][name]
        runs[split] = {
            "manifest": {
                "path": current["manifest_path"].relative_to(REPOSITORY_ROOT).as_posix(),
                "sha256": current["manifest_sha"],
            },
            "original_input_report": {
                "path": current["original_report_path"].relative_to(REPOSITORY_ROOT).as_posix(),
                "sha256": current["original_report_sha"],
            },
            "initial_contour_report": {
                "path": current["current_report_path"].resolve().relative_to(REPOSITORY_ROOT.resolve()).as_posix(),
                "sha256": family_ocr._sha256_bytes(current["current_report_bytes"]),
            },
            "db_geometry_report": {
                "path": current["db_report_path"].relative_to(REPOSITORY_ROOT).as_posix(),
                "sha256": current["db_report_sha"],
            },
            "source_count": len(images), "truth_region_count": truth_count,
            "completed_panel_count": current["stats"]["completed_panels"],
            "failed_source_count": current["stats"]["failed_sources"],
            "failed_panel_count": current["stats"]["failed_panels"],
            "selected_region_count": current["stats"]["selected"],
            "unavailable_baseline_selected_count": current["stats"]["unavailable_baseline_selected"],
        }
    if total_truth != EXPECTED_TOTAL_TRUTHS:
        raise EvidenceError("combined truth count differs from the fixed 329-region denominator")
    for stage in metrics:
        metrics[stage]["combined"] = original_scorer._metrics(
            combined_images, combined_truths, combined_predictions[stage])

    baseline_metrics = original_score["detection_metrics"]["original_model_input"]
    if metrics["raw_expanded"] != baseline_metrics["raw_expanded"]:
        raise EvidenceError("current raw expanded metrics differ from the frozen original-input run")
    complete_identity_verification = (
        aggregate_stats["failed_sources"] == 0
        and aggregate_stats["failed_panels"] == 0
        and aggregate_stats["selected"] == EXPECTED_BASELINE_SELECTED
        and aggregate_stats["unavailable_baseline_selected"] == 0
    )

    result = {
        "schema": OUTPUT_SCHEMA,
        "status": "diagnostic_only_unapproved",
        "scope": "local-synthetic-train-dev-initial-contour-output-diagnostic",
        "model_input": "original",
        "model_input_protocol_sha256": EXPECTED_ORIGINAL_PROTOCOL_SHA256,
        "ocr_output_geometry": "initial_db_contour",
        "geometry_protocol_sha256": protocol_sha,
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
            "candidate": {"path": ORIGINAL_CANDIDATE_PATH.as_posix(), **original_candidate},
            "score": {"path": ORIGINAL_SCORE_PATH.as_posix(), "sha256": EXPECTED_ORIGINAL_SCORE_SHA256},
        },
        "execution_manifest": {
            "path": execution_manifest_path.resolve().relative_to(REPOSITORY_ROOT.resolve()).as_posix(),
            "sha256": family_ocr._sha256_bytes(current_execution_bytes),
            "all_declared_files_verified": True,
            "runtime_assemblies": [
                {"name": name, "sha256": value} for name, value in current_assemblies
            ],
        },
        "supporting_evidence": {
            "original_execution_manifest": {
                "path": ORIGINAL_EXECUTION_PATH.as_posix(),
                "sha256": family_ocr._sha256_bytes(original_execution_bytes),
            },
            "db_execution_manifest": {
                "path": DB_EXECUTION_PATH.as_posix(),
                "sha256": family_ocr._sha256_bytes(db_execution_bytes),
            },
            "counterfactual_helper": {
                "path": COUNTERFACTUAL_HELPER_PATH.as_posix(),
                "sha256": EXPECTED_COUNTERFACTUAL_HELPER_SHA256,
            },
            "counterfactual_report": {
                "path": COUNTERFACTUAL_REPORT_PATH.as_posix(),
                "sha256": EXPECTED_COUNTERFACTUAL_REPORT_SHA256,
            },
            "evaluator_source_dependencies": truth_environment["source_dependencies"],
            "truth_environment": truth_environment,
        },
        "runs": runs,
        "integrity": {
            "protocol_candidate_models_native_licenses_and_execution_files_verified": True,
            "source_and_panel_artifact_bytes_verified_before_truth": True,
            "old_db_reports_and_every_contour_sidecar_verified_before_truth": True,
            "raw_original_input_regions_expanded_geometry_and_confidence_equal_old_atomic_contours": True,
            "consensus_selected_expanded_ids_equal_frozen_original_run_for_completed_panels": True,
            "final_initial_polygons_and_ids_trace_to_atomic_raw_contours": True,
            "truth_created_only_after_all_runtime_and_supporting_evidence_validation": True,
            "truth_sources_packages_and_fonts_authenticated_before_regeneration": True,
            "full_source_truth_count": total_truth,
            "complete_selected_identity_verification": complete_identity_verification,
            "verified_selected_region_count": aggregate_stats["selected"],
            "unavailable_baseline_selected_count": aggregate_stats["unavailable_baseline_selected"],
            "failed_source_count": aggregate_stats["failed_sources"],
            "failed_panel_count": aggregate_stats["failed_panels"],
        },
        "evaluator": {
            "path": "tools/GraphReader.SyntheticRuntimeEvidence/score_initial_contour_output.py",
            "sha256": expected_evaluator,
            "reviewed_bytes_authenticated_before_truth": True,
            "matching": "existing maximum-cardinality one-to-one matching",
            "intersection_over_union_minimum": family_ocr.MATCH_IOU_MINIMUM,
            "truth_denominator": "all 146 train plus 183 development regions, including failed runtime cases",
            "recognition_scored": False, "roles_scored": False,
        },
        "detection_metrics": {
            "initial_contour_output": metrics,
            "frozen_original_model_input": baseline_metrics,
        },
        "recognition_crop_changes": {
            "mapped_region_count": aggregate_stats["selected"],
            "text_changed_count": aggregate_stats["text_changes"],
            "alternatives_changed_count": aggregate_stats["alternative_changes"],
            "role_changed_count_unscored": aggregate_stats["role_changes"],
            "confidence_changed_count_unscored": aggregate_stats["confidence_changes"],
            "initial_to_expanded_width_ratio": _summary(aggregate_stats["width_ratios"]),
            "initial_to_expanded_height_ratio": _summary(aggregate_stats["height_ratios"]),
            "initial_to_expanded_area_ratio": _summary(aggregate_stats["area_ratios"]),
            "interpretation": "Recognition ran on tighter initial-contour crops; changed strings, alternatives, roles, and confidences are descriptive only.",
        },
        "limitations": [
            "This synthetic train/dev result cannot approve a model, production stage, or release.",
            "Recognition strings and roles are reported only as changes because this protocol does not score recognition or role truth.",
            "Failed sources or panels keep their full truth denominator and contribute no predictions; identity verification is marked incomplete when any are unavailable.",
            "The initial contour comparison covers contours accepted by the unchanged DB detector and unchanged consensus selection only.",
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
    parser.add_argument("--evaluator-sha256", required=True)
    arguments = parser.parse_args()
    try:
        result = score(
            arguments.protocol, arguments.candidate, arguments.candidate_sha256,
            arguments.execution_manifest, arguments.execution_manifest_sha256,
            arguments.train_report, arguments.train_report_sha256,
            arguments.dev_report, arguments.dev_report_sha256, arguments.output,
            evaluator_sha256=arguments.evaluator_sha256,
        )
    except (EvidenceError, OSError, KeyError, TypeError, ValueError, json.JSONDecodeError) as exception:
        print(json.dumps({"status": "rejected", "error": str(exception)}, sort_keys=True), file=sys.stderr)
        return 1
    print(json.dumps({
        "status": result["status"],
        "full_source_truth_count": result["integrity"]["full_source_truth_count"],
        "complete_selected_identity_verification": result["integrity"]["complete_selected_identity_verification"],
        "output": str(arguments.output), "production_approval": False,
    }, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
