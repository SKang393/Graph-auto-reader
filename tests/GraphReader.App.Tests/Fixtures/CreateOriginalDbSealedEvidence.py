# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Create a fabricated wire-format fixture, never an acceptance result.

Only pure format functions and the shared policy reference are called. No
admission, model, worker, archive, registry, or sealed input is opened.
Run from the repository root; the App tests embed the resulting JSON.
"""

from __future__ import annotations

import base64
import hashlib
import json
from pathlib import Path
import sys
from types import SimpleNamespace

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT))

from ml.markers.gate_seal import canonical_json_bytes
from ml.policy.ocr_sealed_evaluation import (
    ACCEPTANCE_BARS_SHA256, FULL_OCR_EVIDENCE_SCHEMA, _outcome,
    _preflight_binding, _request,
)
from ml.policy.ocr_sealed_transport import (
    ACCEPTANCE_SCOPE, COVERAGE_PROTOCOL_SHA256, METRIC_REFERENCE_SHA256,
    OcrSealedRequestIdentity, OcrSealedTransportResult, RESULT_SCHEMA,
    validate_result_envelope,
)


def sha(payload: bytes) -> str:
    return hashlib.sha256(payload).hexdigest()


def fake_identity(name: str) -> str:
    return sha(("FABRICATED APP TEST FIXTURE: " + name).encode())


def main() -> None:
    candidate = fake_identity("candidate")
    dev = fake_identity("development score")
    admission = SimpleNamespace(admission_id=fake_identity("admission"))
    attempt, preflight = _preflight_binding(
        SimpleNamespace(schema=FULL_OCR_EVIDENCE_SCHEMA, full_ocr_score_sha256=dev,
                        candidate_sha256=candidate, runtime_identity_sha256=fake_identity("runtime")),
        registry_sha256=fake_identity("registry"), set_id=fake_identity("set"),
        gate_seal=SimpleNamespace(key=fake_identity("gate"), binding={"fixture": "gate"}),
        training_authorization=SimpleNamespace(binding={"fixture": "training"}),
    )
    preflight_bytes = canonical_json_bytes(preflight)
    assert sha(preflight_bytes) == attempt
    request = _request(
        attempt_id=attempt, admission=admission,
        metadata={
            "set_id": fake_identity("set"),
            "archive": {"path": "not-present/fabricated-archive.zip", "sha256": fake_identity("archive")},
            "chain": {"archive_manifest_sha256": fake_identity("archive manifest"), "case_count": 2},
            "scope": {"acceptance_scope": ACCEPTANCE_SCOPE, "coverage_protocol_sha256": COVERAGE_PROTOCOL_SHA256},
        }, candidate_relative="not-present/fabricated-candidate.json", candidate_sha256=candidate,
    )
    request_bytes = canonical_json_bytes(request)

    def geometry(predicted: int) -> dict[str, object]:
        return {"truth_region_count": 140, "predicted_region_count": predicted,
                "true_positives": 133, "false_positives": predicted - 133, "false_negatives": 7,
                "precision": 133 / predicted, "recall": 0.95, "intersection_over_union_minimum": 0.5}

    full = {
        "truth_region_count": 140, "predicted_region_count": 139, "geometry_matched_region_count": 133,
        "geometry_false_positive_count": 6, "geometry_false_negative_count": 7,
        "recognition_exact_count": 133, "recognition_exact_accuracy": 0.95, "truth_character_count": 280,
        "matched_pair_edit_count": 0, "unmatched_truth_deletion_edit_count": 8,
        "unmatched_prediction_insertion_edit_count": 6, "character_error_count": 14,
        "character_error_rate": 0.05, "role_correct_count": 133, "role_accuracy": 0.95,
        "by_expected_runtime_role": {
            role: {"truth_count": 20, "correct_count": 19, "accuracy": 0.95}
            for role in ("annotation", "axistitle", "legendtext", "participant", "phaseheading", "xtick", "ytick")
        }, "intersection_over_union_minimum": 0.5,
    }
    aggregate = {"source_count": 2, "panel_count": 3, "metrics": {
        "raw_detector_geometry": geometry(140), "successfully_recognized_region_geometry": geometry(139),
        "recognition_failures": {"raw_regions_without_successful_recognition": 1}, "full_ocr_metrics": full,
    }}
    expected = OcrSealedRequestIdentity(
        attempt_id=attempt, admission_binding_sha256=admission.admission_id, set_id=request["set_id"],
        candidate_sha256=candidate, archive_sha256=request["archive_sha256"],
        archive_manifest_sha256=request["archive_manifest_sha256"],
        coverage_protocol_sha256=COVERAGE_PROTOCOL_SHA256, request_sha256=sha(request_bytes), source_count=2,
    )
    envelope = {
        "schema": RESULT_SCHEMA, "status": "completed", "split": "sealed", "acceptance_scope": ACCEPTANCE_SCOPE,
        **{name: getattr(expected, name) for name in (
            "attempt_id", "admission_binding_sha256", "set_id", "candidate_sha256", "archive_sha256",
            "archive_manifest_sha256", "coverage_protocol_sha256", "request_sha256")},
        "metric_reference_sha256": METRIC_REFERENCE_SHA256, "execution_provider": "CPUExecutionProvider",
        "cpu_threads": 1, "graph_optimization": "ORT_DISABLE_ALL", "model_inference": True,
        "case_output": False, "production_approved": False, "elapsed_ms": 1000.0, "aggregate": aggregate,
    }
    checked = validate_result_envelope(envelope, expected)
    outcome = _outcome(
        status="pass", read_status="confirmed", admission=admission, attempt_id=attempt,
        preflight_binding_sha256=attempt, preflight_binding=preflight,
        request_relative="not-present/fabricated-request.json", request_sha256=sha(request_bytes),
        aggregate=checked["aggregate"], verdicts={name: True for name in (
            "text_region_detection_precision", "text_region_detection_recall", "recognition_exact_match",
            "character_error_rate", "role_accuracy")},
        transport=OcrSealedTransportResult(checked, sha(b""), 0, 1.0), failure_code=None,
    )
    bars = (ROOT / "ml/policy/acceptance-bars.json").read_bytes()
    assert sha(bars) == ACCEPTANCE_BARS_SHA256
    fixture = {
        "purpose": "Fabricated unit-test counts; not a sealed run or production acceptance result.",
        "candidate_sha256": candidate, "dev_score_sha256": dev,
        "acceptance_bars_base64": base64.b64encode(bars).decode(),
        "request_base64": base64.b64encode(request_bytes).decode(),
        "preflight_base64": base64.b64encode(preflight_bytes).decode(), "outcome": outcome,
        "producer_sources": {name: sha((ROOT / name).read_bytes()) for name in (
            "ml/policy/ocr_sealed_evaluation.py", "ml/policy/ocr_sealed_transport.py", "ml/markers/gate_seal.py")},
    }
    destination = Path(__file__).with_name("original-db-sealed-evidence.json")
    destination.write_bytes((json.dumps(fixture, indent=2, sort_keys=True) + "\n").encode())
    print(json.dumps({"fixture": str(destination), "sha256": sha(destination.read_bytes()),
                      "python_transport_validation": "passed", "sealed_reads": 0}))


if __name__ == "__main__":
    main()
