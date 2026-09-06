# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""One-use truth-hidden public gate for dense-contract V5."""

from __future__ import annotations

import argparse
import base64
from datetime import datetime, timezone
import hashlib
import json
import math
from pathlib import Path

import numpy as np
import onnxruntime as ort

from ml.markers.center.artifact_mask_public_gate import evaluate_public_gate
from ml.markers.center.dense_contract_v5.dataset import PROHIBITED_KINDS, read_archive
from ml.markers.center.dense_contract_v5.train_p1 import (
    ARTIFACT_THRESHOLD,
    HARD_NEGATIVE_TOLERANCE,
    MATCH_TOLERANCE,
    REPO_ROOT,
    REVISION,
    TASK,
    _centers,
    _evaluate,
    _hard_negatives,
    _onnx_outputs,
)
from ml.markers.center.metrics import center_metrics
from ml.markers.center.postprocess import detect_heads
from ml.markers.gate_seal import (
    GateSeal,
    acquire_gate_seal,
    canonical_json_bytes,
    complete_gate_seal,
    sha256_bytes,
    sha256_file,
    void_candidate,
)


ROOT = REPO_ROOT / "ml/markers/center/dense_contract_v5"
GATE_PATH = Path("ml/markers/center/dense_contract_v5/gates/sealed-public-v1.json")
EVALUATOR_SOURCE_PATHS = (
    Path("ml/markers/center/artifact_mask_public_gate.py"),
    Path("ml/markers/center/dense_contract_v5/dataset.py"),
    Path("ml/markers/center/dense_contract_v5/public_gate.py"),
    Path("ml/markers/center/dense_contract_v5/train_p1.py"),
)
GATE_CONFIGURATION = {
    "profile": "marker-center-artifact-mask-public-gate-v1",
    "provider": "CPUExecutionProvider",
    "center_threshold_source": "candidate-report-selected-threshold",
    "artifact_threshold": ARTIFACT_THRESHOLD,
    "match_tolerance_px": MATCH_TOLERANCE,
    "hard_negative_tolerance_px": HARD_NEGATIVE_TOLERANCE,
    "required_exact_fixture_rate": 1.0,
    "required_false_positives": 0,
    "required_false_negatives": 0,
    "required_duplicates": 0,
    "required_prohibited_hits": 0,
}


def _embedded(path: Path, media_type: str) -> dict[str, str]:
    value = path.read_bytes()
    return {
        "media_type": media_type,
        "encoding": "base64",
        "sha256": sha256_bytes(value),
        "content_base64": base64.b64encode(value).decode("ascii"),
    }


def _evaluate_opened_gate(
    *,
    candidate: dict[str, object],
    candidate_report_path: Path,
    onnx_path: Path,
    archive_path: Path,
    dataset_path: Path,
    split_seal_path: Path,
    seal: GateSeal,
) -> dict[str, object]:
    seal.consume_sealed_split()
    archive = read_archive(archive_path)
    session = ort.InferenceSession(str(onnx_path), providers=["CPUExecutionProvider"])
    if session.get_providers() != ["CPUExecutionProvider"]:
        raise RuntimeError("Dense-contract V5 public gate requires CPUExecutionProvider only")
    outputs: list[tuple[np.ndarray, np.ndarray]] = []
    input_stream = hashlib.sha256()
    output_stream = hashlib.sha256()
    for index in range(archive["inputs"].shape[0]):
        value = archive["inputs"][index : index + 1]
        first, second_input, second = _onnx_outputs(session, value)
        outputs.append((first, second))
        input_stream.update(value.tobytes(order="C"))
        input_stream.update(second_input.tobytes(order="C"))
        output_stream.update(first.tobytes(order="C"))
        output_stream.update(second.tobytes(order="C"))
    selected_threshold = float(candidate["selected_threshold"])
    aggregate = _evaluate(archive, outputs, selected_threshold)
    rows = []
    for index, (first, second) in enumerate(outputs):
        combined_artifact = np.maximum(archive["inputs"][index, 2], first[0, 2])
        detections = detect_heads(
            second,
            text_mask=archive["inputs"][index, 1],
            artifact_mask=combined_artifact,
            center_threshold=selected_threshold,
            artifact_threshold=ARTIFACT_THRESHOLD,
        )
        metric = center_metrics(detections, _centers(archive, index), MATCH_TOLERANCE)
        prohibited = {kind: 0 for kind in PROHIBITED_KINDS}
        for kind, x, y in _hard_negatives(archive, index):
            prohibited[kind] += sum(
                math.hypot(item.x - x, item.y - y) <= HARD_NEGATIVE_TOLERANCE
                for item in detections
            )
        rows.append(
            {
                "fixture_id": str(archive["scene_ids"][index]),
                "expected_count": len(_centers(archive, index)),
                "predicted_count": len(detections),
                "false_positive_count": metric.false_positives,
                "false_negative_count": metric.false_negatives,
                "duplicate_count": metric.duplicate_count,
                "prohibited_structure_hits": prohibited,
            }
        )
    metric_report = evaluate_public_gate(rows)
    passed = bool(aggregate["passed"]) and metric_report["status"] == "pass"
    return {
        "schema": "graphreader.marker-artifact-mask-gate.v1",
        "profile": "marker-center-artifact-mask-public-gate-v1",
        "status": "pass" if passed else "fail",
        "scope": "public_synthetic_sealed",
        "provider": "cpu",
        "seed_mask_scope": "ocr_axis_tick_divider_ambiguous_only",
        "coordinate_space": "original_pixels",
        "release_eligible": False,
        "production_approval": False,
        "private_data": False,
        "chandler_used": False,
        "model_sha256": sha256_file(onnx_path),
        "candidate_report_sha256": sha256_file(candidate_report_path),
        "fixture_count": metric_report["fixture_count"],
        "exact_fixture_count": metric_report["exact_fixture_count"],
        "downstream_false_positive_count": metric_report["downstream_false_positive_count"],
        "downstream_false_negative_count": metric_report["downstream_false_negative_count"],
        "downstream_duplicate_count": metric_report["downstream_duplicate_count"],
        "prohibited_structure_hits": metric_report["prohibited_structure_hits"],
        "fixture_results": metric_report["fixture_results"],
        "true_positive_count": metric_report["true_positive_count"],
        "false_positive_count": metric_report["false_positive_count"],
        "false_negative_count": metric_report["false_negative_count"],
        "artifact_precision": metric_report["artifact_precision"],
        "artifact_recall": metric_report["artifact_recall"],
        "prohibited_structure_hit_rate": metric_report["prohibited_structure_hit_rate"],
        "marker_artifact_hits": aggregate["marker_artifact_hits"],
        "direct_execution_inference_calls": archive["inputs"].shape[0] * 2,
        "input_tensor_stream_sha256": input_stream.hexdigest(),
        "output_tensor_stream_sha256": output_stream.hexdigest(),
        "reviewed_resources": {
            "dataset_manifest": _embedded(dataset_path, "application/json"),
            "evaluator_source": _embedded(
                REPO_ROOT / "ml/markers/center/artifact_mask_public_gate.py",
                "text/x-python",
            ),
            "split_seal": _embedded(split_seal_path, "application/json"),
        },
        "public_gate_canonical_seal_key": seal.key,
        "public_gate_opened_seal_sha256": sha256_file(seal.opened_path),
    }


def _run_opened_gate(
    *,
    candidate: dict[str, object],
    candidate_report_path: Path,
    onnx_path: Path,
    archive_path: Path,
    dataset_path: Path,
    split_seal_path: Path,
    seal: GateSeal,
    output_path: Path,
) -> dict[str, object]:
    try:
        report = _evaluate_opened_gate(
            candidate=candidate,
            candidate_report_path=candidate_report_path,
            onnx_path=onnx_path,
            archive_path=archive_path,
            dataset_path=dataset_path,
            split_seal_path=split_seal_path,
            seal=seal,
        )
    except Exception as error:
        failure = {
            "schema": "graphreader.marker-artifact-mask-gate-failure.v1",
            "profile": "marker-center-artifact-mask-public-gate-v1",
            "status": "failed_runner",
            "evaluation_count": 1,
            "release_eligible": False,
            "production_approval": False,
            "private_data": False,
            "chandler_used": False,
            "model_sha256": sha256_file(onnx_path),
            "candidate_report_sha256": sha256_file(candidate_report_path),
            "public_gate_canonical_seal_key": seal.key,
            "public_gate_opened_seal_sha256": sha256_file(seal.opened_path),
            "exception_type": type(error).__name__,
            "exception_message": str(error),
            "completed_utc": datetime.now(timezone.utc).isoformat(),
        }
        output_path.parent.mkdir(parents=True, exist_ok=True)
        output_path.write_bytes(canonical_json_bytes(failure))
        if seal.consumed_path.exists():
            complete_gate_seal(
                seal,
                status="failed_runner",
                report_sha256=sha256_file(output_path),
            )
        else:
            void_candidate(seal, error)
        raise
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_bytes(canonical_json_bytes(report))
    complete_gate_seal(
        seal,
        status=str(report["status"]),
        report_sha256=sha256_file(output_path),
    )
    return report


def run(candidate_report_path: Path, output_path: Path) -> dict[str, object]:
    gate = json.loads((REPO_ROOT / GATE_PATH).read_text(encoding="utf-8"))
    candidate = json.loads(candidate_report_path.read_text(encoding="utf-8"))
    if candidate.get("selection_gate_passed") is not True:
        raise RuntimeError("Public gate requires a passing visible-selection candidate")
    ledger = json.loads(
        (REPO_ROOT / "ml/markers/training-budgets/production-repair-v1.json").read_text(encoding="utf-8")
    )
    entry = next(item for item in ledger["revisions"] if item.get("revision") == REVISION)
    if entry.get("public_gate_authorized") is not True or entry.get("public_gate_authorized_candidate_id") != "P1":
        raise RuntimeError("Dense-contract V5 public gate is not separately authorized")
    onnx_path = REPO_ROOT / candidate["onnx_path"]
    if sha256_file(onnx_path) != candidate["onnx_sha256"]:
        raise RuntimeError("Candidate ONNX changed before public execution")
    dataset_path = ROOT / "PUBLIC_DATASET_MANIFEST.json"
    seal_path = ROOT / "SEALED_PUBLIC_TEST_SEAL.json"
    archive_path = REPO_ROOT / json.loads(seal_path.read_text(encoding="utf-8"))["fixture_archive_path"]
    if sha256_file(archive_path) != gate["expected_public_fixture_archive_sha256"]:
        raise RuntimeError("Truth-hidden public archive changed")
    seal = acquire_gate_seal(
        repo_root=REPO_ROOT,
        task=TASK,
        revision=str(gate["revision"]),
        candidate_hashes={
            "candidate_report_sha256": sha256_file(candidate_report_path),
            "onnx_sha256": sha256_file(onnx_path),
        },
        dataset_manifest_sha256=sha256_file(dataset_path),
        split_config_path=GATE_PATH,
        evaluator_source_paths=EVALUATOR_SOURCE_PATHS,
        gate_config=GATE_CONFIGURATION,
    )
    return _run_opened_gate(
        candidate=candidate,
        candidate_report_path=candidate_report_path,
        onnx_path=onnx_path,
        archive_path=archive_path,
        dataset_path=dataset_path,
        split_seal_path=seal_path,
        seal=seal,
        output_path=output_path,
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--candidate-report", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    report = run(arguments.candidate_report.resolve(), arguments.output.resolve())
    print(json.dumps(report, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
