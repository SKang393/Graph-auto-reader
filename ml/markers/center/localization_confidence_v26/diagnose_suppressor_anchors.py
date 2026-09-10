# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Freeze and attribute fixed V25 synthetic-dev NMS suppressor anchors."""

from __future__ import annotations

from collections import Counter
import hashlib
import json
import math
from pathlib import Path
import time
from typing import Any, Mapping, Sequence

import numpy as np
import onnxruntime as ort
import torch

from ml.markers.center.plot_domain_v25 import diagnose_dev as v25_diagnosis
from ml.markers.center.plot_domain_v25 import protocol, runtime_domain_binding_v3, train_p1
from ml.markers.center.plot_domain_v25.proposal_domain import extract_proposals_in_domain
from ml.markers.center.real_range_generator_v1.generator import build_split
from ml.markers.gate_seal import canonical_json_bytes, source_bundle_sha256


REPOSITORY_ROOT = Path(__file__).resolve().parents[4]
OUTPUT_DIRECTORY = Path(
    "artifacts/goal22-runs/marker-v26-target-preflight/suppressor-anchor-diagnosis-v1"
)
CACHE_NAME = "fixed-v25-dev-outputs.npz"
REPORT_NAME = "diagnosis.json"
SCHEMA = "graphreader.marker-center-v25-suppressor-anchor-diagnosis.v1"


def _sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _array_sha(value: np.ndarray) -> str:
    array = np.ascontiguousarray(value)
    metadata = canonical_json_bytes({"dtype": str(array.dtype), "shape": list(array.shape)})
    digest = hashlib.sha256()
    digest.update(len(metadata).to_bytes(8, "little"))
    digest.update(metadata)
    digest.update(array.tobytes(order="C"))
    return digest.hexdigest()


def _anchor_context(
    scene: Any,
    proposal_coordinates: torch.Tensor,
    output: np.ndarray,
    candidate: v25_diagnosis.Candidate,
    missed_truth_index: int,
) -> dict[str, object]:
    """Describe one output using the exact float32 V25 training assignment."""

    centers = torch.tensor(scene.centers, dtype=torch.float32).reshape(-1, 2)
    distances = torch.cdist(proposal_coordinates, centers)
    nearest_distance, nearest_truth = distances.min(dim=1)
    index = candidate.source_proposal_index
    assigned_truth = int(nearest_truth[index])
    training_positive = bool(nearest_distance[index].le(3.0))
    if not training_positive:
        relation = "negative"
    elif assigned_truth == missed_truth_index:
        relation = "positive_for_missed_truth"
    else:
        relation = "positive_for_other_truth"
    anchor_x, anchor_y = (float(value) for value in proposal_coordinates[index].tolist())
    prediction = candidate.prediction
    truth_x, truth_y = scene.centers[missed_truth_index]
    return {
        "proposal_index": index,
        "anchor": {"x": anchor_x, "y": anchor_y},
        "anchor_distance_to_missed_truth_px": math.hypot(anchor_x - truth_x, anchor_y - truth_y),
        "training_nearest_truth_index": assigned_truth,
        "training_nearest_truth_distance_px": float(nearest_distance[index]),
        "training_anchor_relation": relation,
        "model_offset_grid": {"x": float(output[index, 1]), "y": float(output[index, 2])},
        "decoded_offset_px": {
            "x": float(output[index, 1]) * 4.0,
            "y": float(output[index, 2]) * 4.0,
        },
        "decoded_center": {"x": prediction.x, "y": prediction.y},
        "decoded_distance_to_missed_truth_px": math.hypot(
            prediction.x - truth_x, prediction.y - truth_y
        ),
        "confidence": prediction.confidence,
        "model_output_radius_px": float(output[index, 3]),
        "decoded_radius_px": prediction.radius,
    }


def _nms_records(
    split_name: str,
    scene_identity: str,
    scene: Any,
    proposals: Any,
    output: np.ndarray,
    trace: v25_diagnosis.PostprocessTrace,
    match_pairs: Sequence[tuple[int, int]],
) -> tuple[list[dict[str, object]], Counter[str], Counter[str]]:
    matched_truths = {truth for _, truth in match_pairs}
    matched_predictions = {prediction for prediction, _ in match_pairs}
    accepted_index = {
        candidate.source_proposal_index: index for index, candidate in enumerate(trace.accepted)
    }
    suppression_by_index = {
        item.suppressed.source_proposal_index: item for item in trace.suppressions
    }
    records: list[dict[str, object]] = []
    suppressor_labels: Counter[str] = Counter()
    suppressor_outcomes: Counter[str] = Counter()
    truth_radii = v25_diagnosis._truth_radii(scene)
    for truth_index, (truth_x, truth_y) in enumerate(scene.centers):
        if truth_index in matched_truths:
            continue
        eligible = [
            candidate for candidate in trace.candidates_before_nms
            if math.hypot(candidate.prediction.x - truth_x, candidate.prediction.y - truth_y)
            <= v25_diagnosis.MATCH_TOLERANCE_PX
        ]
        if not eligible or any(
            candidate.source_proposal_index in accepted_index for candidate in eligible
        ):
            continue
        if any(candidate.source_proposal_index not in suppression_by_index for candidate in eligible):
            raise RuntimeError("fixed NMS trace lost a suppressor for an eligible candidate")
        lowest = min(
            eligible,
            key=lambda candidate: (
                math.hypot(candidate.prediction.x - truth_x, candidate.prediction.y - truth_y),
                -candidate.prediction.confidence,
                candidate.source_proposal_index,
            ),
        )
        selected_suppression = suppression_by_index[lowest.source_proposal_index]
        suppressor = selected_suppression.suppressor
        lowest_record = _anchor_context(
            scene, proposals.coordinates, output, lowest, truth_index
        )
        suppressor_record = _anchor_context(
            scene, proposals.coordinates, output, suppressor, truth_index
        )
        suppressor_index = accepted_index[suppressor.source_proposal_index]
        suppressor_outcome = (
            "true_positive" if suppressor_index in matched_predictions else "false_positive"
        )
        suppressor_labels[str(suppressor_record["training_anchor_relation"])] += 1
        suppressor_outcomes[suppressor_outcome] += 1
        records.append({
            "split": split_name,
            "scene_identity": scene_identity,
            "truth_index": truth_index,
            "truth": {
                "x": float(truth_x),
                "y": float(truth_y),
                "radius_px": truth_radii[truth_index],
                "radius_category": v25_diagnosis.radius_bin(truth_radii[truth_index]),
            },
            "eligible_candidate_count": len(eligible),
            "eligible_candidates": [
                _anchor_context(scene, proposals.coordinates, output, candidate, truth_index)
                for candidate in sorted(
                    eligible,
                    key=lambda candidate: candidate.source_proposal_index,
                )
            ],
            "lowest_error_candidate": lowest_record,
            "actual_suppressor": suppressor_record,
            "actual_suppressor_prediction_outcome": suppressor_outcome,
            "suppressed_to_suppressor_distance_px": selected_suppression.center_distance,
            "nms_exclusion_distance_px": selected_suppression.exclusion_distance,
        })
    return records, suppressor_labels, suppressor_outcomes


def _suppressor_cross_tabs(records: Sequence[Mapping[str, Any]]) -> dict[str, dict[str, int]]:
    fp_by_relation_and_truth_radius: Counter[str] = Counter()
    fp_by_relation_and_output_radius: Counter[str] = Counter()
    eligible_relations: Counter[str] = Counter()
    for record in records:
        eligible_relations.update(
            str(candidate["training_anchor_relation"])
            for candidate in record["eligible_candidates"]
        )
        if record["actual_suppressor_prediction_outcome"] != "false_positive":
            continue
        suppressor = record["actual_suppressor"]
        relation = str(suppressor["training_anchor_relation"])
        fp_by_relation_and_truth_radius[
            f"{relation}|{record['truth']['radius_category']}"
        ] += 1
        fp_by_relation_and_output_radius[
            f"{relation}|{v25_diagnosis.decoded_radius_bin(float(suppressor['model_output_radius_px']))}"
        ] += 1
    return {
        "eligible_candidate_anchor_training_relation": dict(sorted(eligible_relations.items())),
        "false_positive_suppressor_by_anchor_relation_and_missed_truth_radius": dict(
            sorted(fp_by_relation_and_truth_radius.items())
        ),
        "false_positive_suppressor_by_anchor_relation_and_output_radius": dict(
            sorted(fp_by_relation_and_output_radius.items())
        ),
    }


def _diagnose_split(
    name: str,
    bound_scenes: Sequence[Any],
    session: ort.InferenceSession,
    cache: dict[str, np.ndarray],
) -> dict[str, object]:
    totals: Counter[str] = Counter()
    records: list[dict[str, object]] = []
    suppressor_labels: Counter[str] = Counter()
    suppressor_outcomes: Counter[str] = Counter()
    cache_manifest: list[dict[str, object]] = []
    forward_ms = 0.0
    for scene_number, bound in enumerate(bound_scenes):
        scene = bound.scene if hasattr(bound, "scene") else bound
        domain = bound.panel_domain.domain if hasattr(bound, "panel_domain") else None
        proposals = (
            extract_proposals_in_domain(scene.tensor, domain).proposals
            if domain is not None
            else v25_diagnosis.v24.extract_proposals(scene.tensor)
        )
        started = time.perf_counter()
        output = v25_diagnosis._infer(session, proposals.patches)
        forward_ms += (time.perf_counter() - started) * 1000.0
        trace = v25_diagnosis._postprocess_trace(scene, proposals, output, domain)
        matches = v25_diagnosis.greedy_match_pairs(trace.accepted, scene.centers)
        scene_identity = (
            scene.sampling_identity
            if hasattr(scene, "sampling_identity")
            else f"{scene.split}:{scene.family}:{scene.seed}"
        )
        scene_records, labels, outcomes = _nms_records(
            name, scene_identity, scene, proposals, output, trace, matches
        )
        records.extend(scene_records)
        suppressor_labels.update(labels)
        suppressor_outcomes.update(outcomes)
        totals.update({
            "scene_count": 1,
            "truth_count": len(scene.centers),
            "prediction_count": len(trace.accepted),
            "true_positive": len(matches),
            "false_positive": len(trace.accepted) - len(matches),
            "false_negative": len(scene.centers) - len(matches),
        })
        coordinates_array = proposals.coordinates.detach().cpu().numpy().astype(np.float32, copy=False)
        coordinate_key = f"{name}_{scene_number:03d}_proposal_coordinates"
        output_key = f"{name}_{scene_number:03d}_candidate_predictions"
        cache[coordinate_key] = coordinates_array
        cache[output_key] = output
        cache_manifest.append({
            "scene_identity": scene_identity,
            "proposal_coordinates_key": coordinate_key,
            "proposal_coordinates_sha256": _array_sha(coordinates_array),
            "candidate_predictions_key": output_key,
            "candidate_predictions_sha256": _array_sha(output),
            "proposal_count": len(output),
        })

    expected = v25_diagnosis.EXPECTED[name]
    exact = {
        "scene_count": expected["scenes"],
        "truth_count": expected["truth"],
        "true_positive": expected["tp"],
        "false_positive": expected["fp"],
        "false_negative": expected["fn"],
    }
    for key, value in exact.items():
        if totals[key] != value:
            raise RuntimeError(f"{name} fixed metric changed: {key}")
    expected_nms = 27 if name == "component" else 2
    if len(records) != expected_nms:
        raise RuntimeError(f"{name} NMS missed-truth count changed")
    cross_tabs = _suppressor_cross_tabs(records)
    return {
        "counts": dict(sorted(totals.items())),
        "nms_missed_truth_count": len(records),
        "actual_suppressor_anchor_training_relation": dict(sorted(suppressor_labels.items())),
        "actual_suppressor_prediction_outcome": dict(sorted(suppressor_outcomes.items())),
        **cross_tabs,
        "records": records,
        "cache_manifest": cache_manifest,
        "model_forward_ms": round(forward_ms, 3),
    }


def run(
    *,
    repository_root: Path = REPOSITORY_ROOT,
    output_directory: Path = OUTPUT_DIRECTORY,
) -> dict[str, object]:
    started = time.perf_counter()
    root = repository_root.resolve()
    destination = output_directory if output_directory.is_absolute() else root / output_directory
    if destination.exists():
        raise FileExistsError(f"diagnostic output already exists: {destination}")
    paths = {
        "result": root / v25_diagnosis.RESULT_PATH,
        "config": root / v25_diagnosis.CONFIG_PATH,
        "candidate_report": root / v25_diagnosis.CANDIDATE_REPORT_PATH,
        "onnx": root / v25_diagnosis.ONNX_PATH,
        "binding": root / protocol.FAMILY_BINDING_PATH,
        "prior_diagnosis": root / v25_diagnosis.OUTPUT_PATH,
        "prior_diagnostic_source": Path(v25_diagnosis.__file__).resolve(),
    }
    expected = {
        "result": v25_diagnosis.EXPECTED_RESULT_SHA256,
        "config": v25_diagnosis.EXPECTED_CONFIG_SHA256,
        "candidate_report": v25_diagnosis.EXPECTED_REPORT_SHA256,
        "onnx": v25_diagnosis.EXPECTED_ONNX_SHA256,
        "binding": protocol.FAMILY_BINDING_SHA256,
        "prior_diagnosis": "812c459440ec8f7ec7276305accbc5683f230acb7d9355ba64af1398b8b75212",
        "prior_diagnostic_source": "7d138c9cb7d29bb97f2d88d7b76f15e90f9bcda32817850d54e263e8203a0bb2",
    }
    actual = {key: _sha(path) for key, path in paths.items()}
    if actual != expected:
        raise RuntimeError("fixed V25 suppressor diagnostic input identity changed")
    config = json.loads(paths["config"].read_bytes())
    result = json.loads(paths["result"].read_bytes())
    candidate_report = json.loads(paths["candidate_report"].read_bytes())
    if (
        result.get("candidate_report_sha256") != expected["candidate_report"]
        or candidate_report.get("candidate_config_sha256") != expected["config"]
        or candidate_report.get("onnx_sha256") != expected["onnx"]
        or config.get("confidence_threshold") != v25_diagnosis.THRESHOLD
    ):
        raise RuntimeError("fixed V25 result/config/model relationship changed")
    runner_bundle = source_bundle_sha256(root, train_p1.RUNNER_SOURCE_PATHS)
    if runner_bundle != config.get("expected_runner_source_bundle_sha256"):
        raise RuntimeError("fixed V25 runner source bundle changed")

    domains = runtime_domain_binding_v3.load_runtime_domain_binding_v3(
        paths["binding"], expected["binding"], repository_root=root
    )
    joined = runtime_domain_binding_v3.join_runtime_domain_truth_v3(domains, repository_root=root)
    component = tuple(build_split("dev", independent_layout=True))
    family = tuple(joined.dev)
    if len(component) != 167 or sum(len(scene.centers) for scene in component) != 2004:
        raise RuntimeError("component dev denominator changed")
    if len(family) != 9 or sum(len(item.scene.centers) for item in family) != 206:
        raise RuntimeError("family dev denominator changed")

    options = ort.SessionOptions()
    options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    session = ort.InferenceSession(
        str(paths["onnx"]), sess_options=options, providers=["CPUExecutionProvider"]
    )
    if session.get_providers()[0] != "CPUExecutionProvider":
        raise RuntimeError("diagnostic did not select CPUExecutionProvider")
    cache: dict[str, np.ndarray] = {}
    component_report = _diagnose_split("component", component, session, cache)
    family_report = _diagnose_split("family", family, session, cache)

    destination.mkdir(parents=True, exist_ok=False)
    cache_path = destination / CACHE_NAME
    np.savez_compressed(cache_path, **cache)
    report: dict[str, object] = {
        "schema": SCHEMA,
        "scope": "fixed-v25-synthetic-dev-suppressor-anchor-diagnosis",
        "candidate_id": "P1",
        "operating_threshold": v25_diagnosis.THRESHOLD,
        "inputs": {
            **{f"{key}_path": path.relative_to(root).as_posix() for key, path in paths.items()},
            **{f"{key}_sha256": value for key, value in actual.items()},
            "runner_source_bundle_sha256": runner_bundle,
            "diagnostic_source_sha256": _sha(Path(__file__)),
        },
        "cache": {
            "path": CACHE_NAME,
            "sha256": _sha(cache_path),
            "array_count": len(cache),
            "contains": "synthetic dev proposal coordinates and fixed V25 candidate outputs only",
        },
        "component_dev": component_report,
        "family_dev": family_report,
        "interpretation_limits": [
            "Training-anchor relation replays the exact float32 nearest-truth and <=3px V25 labeling rule.",
            "A negative suppressor can still share local visual features with markers; this report identifies supervision, not causal capacity.",
            "The proposed unique-nearest positive target cannot directly repair a suppressor whose anchor was trained negative.",
            "No threshold, NMS rule, radius contract, model, revision, private, sealed, or production decision is changed.",
        ],
        "synthetic_only": True,
        "optimizer_steps_run": 0,
        "private_reads": 0,
        "sealed_runs": 0,
        "production_approval": False,
        "elapsed_ms": round((time.perf_counter() - started) * 1000.0, 3),
    }
    (destination / REPORT_NAME).write_bytes(canonical_json_bytes(report))
    return report


def main() -> int:
    report = run()
    print(json.dumps({
        "status": "complete",
        "component_nms": report["component_dev"]["nms_missed_truth_count"],
        "family_nms": report["family_dev"]["nms_missed_truth_count"],
        "output": str(OUTPUT_DIRECTORY / REPORT_NAME),
    }, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
