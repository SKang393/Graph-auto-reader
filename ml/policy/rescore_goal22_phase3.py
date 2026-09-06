# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Aggregate-only Goal 22 Phase 3 rescore.

This module reads recorded JSON metrics and hashes only. It never opens a
fixture archive, invokes ONNX Runtime, trains, or reads case-level outputs.
"""

from __future__ import annotations

import hashlib
import json
from pathlib import Path
from typing import Any


REPO_ROOT = Path(__file__).resolve().parents[2]
POLICY_PATH = REPO_ROOT / "ml/policy/acceptance-bars.json"
RESULT_PATH = REPO_ROOT / "ml/policy/goal22-phase3-rescore-result.json"
TRAINING_BUDGET_PATH = REPO_ROOT / "ml/markers/training-budgets/production-repair-v1.json"
MARKER_ADAPTER_PATH = REPO_ROOT / "src/GraphReader.App/Integration/Workflow/ProductionProposalMarkerCenterAdapter.cs"
MARKER_ADAPTER_TEST_PATH = REPO_ROOT / "tests/GraphReader.App.Tests/ProductionProposalMarkerCenterAdapterTests.cs"


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _read(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def _canonical_sha256(value: object) -> str:
    encoded = (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


OCR_CANDIDATES: tuple[dict[str, Any], ...] = (
    {
        "name": "V8 public production composition aggregate",
        "task": "ocr-detection-recognition",
        "revision": "graphreader-v10-bounded-zero-consensus-ambiguity-alias-composition-v8",
        "candidate_id": "P1",
        "evidence_split": "sealed",
        "result_path": "ml/ocr/production_composition_v8/PUBLIC_GATE_REPORT.json",
        "result_sha256": "43384271fefedab374613141a858367b00d86a14d41b1d0994a66d602f6329b4",
        "metrics_path": ("metrics",),
        "selected_threshold": 0.95,
        "operating_configuration": {
            "detector_threshold": 0.95,
            "official_rescue_threshold": 0.90,
            "consensus_rescue_threshold": 0.85,
            "zero_consensus_rescue_threshold": 0.82,
            "numeric_minimum_confidence": 0.65,
        },
        "threshold_source_path": "ml/ocr/production_composition_v8/PROTOCOL.json",
        "threshold_source_sha256": "32b4a7f74bfe93eba01ae59f0a6eb7cd575e019c605bb182119b67da2b7b25d0",
        "threshold_source_field_path": ("models", "detector", "threshold"),
        "protocol_path": "ml/ocr/production_composition_v8/PROTOCOL.json",
        "protocol_sha256": "32b4a7f74bfe93eba01ae59f0a6eb7cd575e019c605bb182119b67da2b7b25d0",
        "adapter_factory_path": "src/GraphReader.Ocr/OcrV8ProductionCompositionFactory.cs",
        "adapter_factory_sha256": "883db3c175362c265c92775475286a6263907f99e37ad11e62a4abd8f9230399",
        "payloads": (
            ("detector", "ml/ocr/component_spaced_recall_detector_v10/artifacts/P2-run/graph-text-spaced-component-recall-v10-p2.onnx", "474b8468dbd91416f4e4978dafc46cb2317775d59d821c0470e0cd3e0f6203db"),
            ("official_recognizer", "ml/ocr/official_bakeoff/runs/conversion/en_PP-OCRv5_mobile_rec.onnx", "7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743"),
            ("official_recognizer_inference_yaml", "ml/ocr/official_bakeoff/runs/extracted/en_PP-OCRv5_mobile_rec_infer/inference.yml", "27e91d0582f40168aa218303c76e184bc78fa7a5d105aad0cfbad8458b441067"),
            ("numeric_recognizer", "ml/ocr/component_ensemble_v5/artifacts/P1-run/graph-numeric-component-ensemble-v5-p1.onnx", "9db95c41ce396e8b2dff3b525556615528a00ca87f4cc531274374b961417c84"),
            ("ambiguity_recognizer", "ml/ocr/ambiguity_source_group_classifier_v3/artifacts/P2-run/graph-ambiguity-source-group-v3-p2.onnx", "b8e2773ca3966469081875fc36b3981ef4eb458356d8dfdae2be2722602f0096"),
        ),
    },
    {
        "name": "V18 P1 selection aggregate",
        "task": "ocr-detection-recognition",
        "revision": "graph-text-recognition-confirmed-proposal-role-v18",
        "candidate_id": "P1",
        "evidence_split": "selection",
        "result_path": "ml/ocr/recognition_confirmed_proposal_role_v18/P1_RESULT.json",
        "result_sha256": "8b77be5cf32db4d7519035476dd155f1726fd545bfca0014d82d8a2922e38dba",
        "metrics_path": ("metrics",),
        "selected_threshold": 0.64,
        "threshold_source_path": "ml/ocr/recognition_confirmed_proposal_role_v18/FEASIBILITY_EVIDENCE.json",
        "threshold_source_sha256": "cd309eb202cffe97f6b43ef2d337a300eb3da955e8a85d0b0c10e6d1f425164e",
        "threshold_source_field": "detector_probability_threshold",
        "payloads": (
            ("detector", "ml/ocr/structural_veto_proposal_role_v17/artifacts/P3-run/graph-text-structural-veto-proposal-role-v17-p3.onnx", "ca32487f1df2c3fea1b8c2f51daf7578ed9756e9140d1b0eaf2a16b283591262"),
            ("recognizer", "ml/ocr/official_bakeoff/runs/conversion/en_PP-OCRv5_mobile_rec.onnx", "7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743"),
        ),
    },
    {
        "name": "V28 P3 public aggregate",
        "task": "ocr-detection-recognition",
        "revision": "graph-text-relational-neighborhood-proposal-v28",
        "candidate_id": "P3",
        "evidence_split": "sealed",
        "result_path": "ml/ocr/relational_neighborhood_proposal_v28/PUBLIC_GATE_RESULT.json",
        "result_sha256": "e5ed3ed21c66f3bc3e0e6789d099c720f907b97a14a26f1433b80a35381e630b",
        "metrics_path": ("metrics",),
        "selected_threshold": 0.55,
        "threshold_source_field": "selected_threshold",
        "payloads": (
            ("onnx", "ml/ocr/relational_neighborhood_proposal_v28/artifacts/P3-run/graph-text-relational-neighborhood-proposal-v28-p3.onnx", "4179534c1abfe7dd22e041d452d52269550f8a13471cbd18bacb0becd18b45af"),
        ),
    },
    {
        "name": "V29 P1 public aggregate",
        "task": "ocr-detection-recognition",
        "revision": "graph-text-dual-route-consensus-proposal-v29",
        "candidate_id": "P1",
        "evidence_split": "sealed",
        "result_path": "ml/ocr/dual_route_consensus_proposal_v29/PUBLIC_GATE_RESULT.json",
        "result_sha256": "db2ceff2bf73cc37804fca76280f90b6112bcc79165910add87755297bf7d0f8",
        "metrics_path": ("metrics",),
        "selected_threshold": 0.55,
        "threshold_source_field": "selected_threshold",
        "payloads": (
            ("onnx", "ml/ocr/dual_route_consensus_proposal_v29/artifacts/P1-run/graph-text-dual-route-consensus-proposal-v29-p1.onnx", "a1ce725897f44d43a6db0852638abb3787c9be917bba0d412f0b1a798831f223"),
        ),
    },
    {
        "name": "V30 P1 public aggregate",
        "task": "ocr-detection-recognition",
        "revision": "graph-text-unanimous-structure-veto-v30",
        "candidate_id": "P1",
        "evidence_split": "sealed",
        "result_path": "ml/ocr/unanimous_structure_veto_v30/PUBLIC_GATE_RESULT.json",
        "result_sha256": "c070c1acd1b803f579529055949e363b97d24cb4207233a35d86b87b2e691e3c",
        "metrics_path": ("metrics",),
        "selected_threshold": 0.55,
        "threshold_source_field": "selected_threshold",
        "payloads": (
            ("v17_detector", "ml/ocr/structural_veto_proposal_role_v17/artifacts/P3-run/graph-text-structural-veto-proposal-role-v17-p3.onnx", "ca32487f1df2c3fea1b8c2f51daf7578ed9756e9140d1b0eaf2a16b283591262"),
            ("official_recognizer", "ml/ocr/official_bakeoff/runs/conversion/en_PP-OCRv5_mobile_rec.onnx", "7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743"),
            ("official_recognizer_inference_yaml", "ml/ocr/official_bakeoff/runs/extracted/en_PP-OCRv5_mobile_rec_infer/inference.yml", "27e91d0582f40168aa218303c76e184bc78fa7a5d105aad0cfbad8458b441067"),
            ("onnx", "ml/ocr/unanimous_structure_veto_v30/artifacts/P1-run/graph-text-unanimous-structure-veto-v30-p1.onnx", "78425c5b4a45ef2cbf99086243af0ede96c91b2b6afcdac1daa71bfeb5e55c18"),
        ),
    },
    {
        "name": "V31 P2 dev aggregate",
        "task": "ocr-detection-recognition",
        "revision": "graph-text-robust-quorum-recall-v31",
        "candidate_id": "P2",
        "evidence_split": "dev",
        "result_path": "ml/ocr/robust_quorum_recall_v31/P2_RESULT.json",
        "result_sha256": "34106e7a018be2964d733162b27292cef5db9bb448eaf3e999accbbd6065c4a3",
        "metrics_path": ("selected_threshold_metrics",),
        "scene_count_path": ("scene_count",),
        "selected_threshold": 0.75,
        "threshold_source_field": "selected_threshold",
        "payloads": (
            ("onnx", "ml/ocr/robust_quorum_recall_v31/artifacts/P2-run/graph-text-robust-quorum-recall-v31-p2.onnx", "98ff06aef445cbdb0a9c7a7a376ee5b0eea51c691610ba0d4fe72203225b976a"),
        ),
    },
)


MARKER_CANDIDATES: tuple[dict[str, Any], ...] = (
    {
        "name": "Marker production-repair-v2 P3 public aggregate",
        "task": "marker-center",
        "revision": "marker-center-production-repair-v2",
        "candidate_id": "P3",
        "evidence_split": "sealed",
        "result_path": "ml/markers/training-budgets/production-repair-v1.json",
        "ledger_entry_sha256": "87ab2e662531772af92529a92dd69269522134f4cd5132c283b85930ded6d1bd",
        "aggregate": {"scene_count": 3, "exact_scene_count": 2, "true_positives": 18, "false_positives": 1, "false_negatives": 0, "prohibited_structure_hits": 0},
        "aggregate_report_sha256": "6f92923bcc54dd60e4cb0e69a2ad759437abc99c0314e496c37b4c285bd99386",
        "ledger_revision": "marker-center-production-repair-v2",
        "threshold_source_path": "ml/markers/center/training/production-repair-v2-p3.json",
        "threshold_source_sha256": "2526956577d6db282ca49e24921d25bfd8f5f0e5b64c55d8b384c266f5889d67",
        "threshold_source_field_path": ("changes", "mask_consensus_threshold"),
        "selected_threshold": 0.2,
        "optional_evidence_path": "ml/markers/center/artifacts/production-public-gate-v2-p3-20260804/public-gate-report.json",
        "optional_evidence_sha256": "6f92923bcc54dd60e4cb0e69a2ad759437abc99c0314e496c37b4c285bd99386",
        "payload_available_at_phase3_run": False,
        "payload_reason": "No selected production-repair-v2 P3 payload is present in the current promotion context.",
    },
    {
        "name": "Marker normalized-training-v4 P1 public aggregate",
        "task": "marker-center",
        "revision": "marker-center-normalized-training-v4",
        "candidate_id": "P1",
        "evidence_split": "sealed",
        "result_path": "ml/markers/training-budgets/production-repair-v1.json",
        "ledger_entry_sha256": "08159eca15106683dd3272d8f51b0df9497d618fecb4e4946bc532f2107f92af",
        "ledger_revision": "marker-center-normalized-training-v4",
        "aggregate_fields": {"scene_count": "p1_public_scene_count", "exact_scene_count": "p1_public_exact_scene_count", "true_positives": "p1_public_true_positives", "false_positives": "p1_public_false_positives", "false_negatives": "p1_public_false_negatives", "prohibited_structure_hits": "p1_public_prohibited_structure_hits"},
        "selected_threshold": 0.6,
        "threshold_source_field": "p1_selected_threshold",
        "payload_available_at_phase3_run": False,
        "payload_reason": "Public aggregate is recorded, but no payload is eligible for this rescore selection.",
    },
    {
        "name": "Marker runtime-consistency-v2 P2 public aggregate",
        "task": "marker-center",
        "revision": "marker-center-runtime-consistency-v2",
        "candidate_id": "P2",
        "evidence_split": "sealed",
        "result_path": "ml/markers/training-budgets/production-repair-v1.json",
        "ledger_entry_sha256": "fd14d09904b72cd9411ea5563fd0a5700d301357e549848fb296254afba1f110",
        "ledger_revision": "marker-center-runtime-consistency-v2",
        "aggregate_fields": {"scene_count": "p2_public_scene_count", "exact_scene_count": "p2_public_exact_scene_count", "true_positives": "p2_public_true_positives", "false_positives": "p2_public_false_positives", "false_negatives": "p2_public_false_negatives", "prohibited_structure_hits": "p2_public_prohibited_structure_hits"},
        "selected_threshold": 0.25,
        "threshold_source_field": "p2_selected_threshold",
        "optional_evidence_path": "ml/markers/center/artifacts/runtime-consistency-v2/P2-run/public-gate-report.json",
        "optional_evidence_sha256": "9013f187982c6f8e492d6cfbbbd28214f116f21e268ca35ad07526ca014ba5dd",
        "payload_available_at_phase3_run": True,
        "payload_path": "ml/markers/center/artifacts/runtime-consistency-v2/P2-run/marker-center-runtime-consistency-p2.onnx",
        "payload_sha256": "924c555e2f27955c644143125d7abd3b05859ea9928ab9d1e741e0544fa19e8b",
    },
)


APPROVED_MARKER_CLASSIFIER = {
    "name": "Approved marker classifier public-v3 compatibility review",
    "task": "marker-classifier",
    "model_id": "graph-marker-classifier",
    "model_version": "0.1.0",
    "evidence_path": "artifacts/production-model-store/evidence/graph-marker-classifier/0.1.0/marker-classifier-production-approval.json",
    "evidence_file_sha256": "c4fb25e45e9c6d77100de8230a30443231445fa71751d685ba66c65da370e7a3",
    "public_v3_report_path": "ml/markers/classifier/artifacts/production-runtime-public-v3-p1-20260804/gate-report.json",
    "public_v3_report_sha256": "32eed939875a3f6a3465fe8cf42a7f9f1ab9c33a4e5b225dfb7c396de2741757",
    "payload_sha256": "26f9304f1689053a0b94aa896a1e239f6ade1e5c1920736a3535c1b32f803b8a",
    "optional_payload_path": "artifacts/production-model-store/runtime/graph-marker-classifier/0.1.0/marker-classifier-probability-packed.onnx",
    "prior_production_approval": True,
    "shape_accuracy": 0.9907407407407407,
    "fill_accuracy": 0.9444444444444444,
}


def _metric(record: dict[str, Any], candidate: dict[str, Any]) -> dict[str, int]:
    if "aggregate" in candidate:
        if "ledger_revision" in candidate:
            ledger_entry = next(item for item in record["revisions"] if item["revision"] == candidate["ledger_revision"])
            if ledger_entry["p3_public_gate_report_sha256"] != candidate["aggregate_report_sha256"]:
                raise RuntimeError(f"Aggregate snapshot source identity mismatch: {candidate['revision']}")
            ledger_metrics = {
                "scene_count": ledger_entry["p3_public_gate_scene_count"],
                "exact_scene_count": ledger_entry["p3_public_gate_exact_scene_count"],
                "true_positives": ledger_entry["p3_public_gate_true_positives"],
                "false_positives": ledger_entry["p3_public_gate_false_positives"],
                "false_negatives": ledger_entry["p3_public_gate_false_negatives"],
                "prohibited_structure_hits": ledger_entry["p3_public_gate_prohibited_structure_hits"],
            }
            for key, value in ledger_metrics.items():
                if int(candidate["aggregate"][key]) != int(value):
                    raise RuntimeError(f"Aggregate snapshot value mismatch: {candidate['revision']}:{key}")
        return {key: int(value) for key, value in candidate["aggregate"].items()}
    if "ledger_revision" in candidate:
        ledger_entries = record["revisions"]
        record = next(item for item in ledger_entries if item["revision"] == candidate["ledger_revision"])
    metrics: dict[str, Any] = record
    for key in candidate.get("metrics_path", ()):
        metrics = metrics[key]
    fields = candidate.get("aggregate_fields", {})
    if fields:
        return {key: int(record[value]) for key, value in fields.items()}
    scene_count = int(record.get("scene_count", metrics.get("scene_count")))
    if "scene_count_path" in candidate:
        scene_count = int(record[candidate["scene_count_path"][0]])
    return {
        "scene_count": scene_count,
        "exact_scene_count": int(metrics.get("exact_scene_count", metrics.get("exact_detection_scene_count", record.get("exact_scene_count", 0)))),
        "true_positives": int(metrics["true_positives"]),
        "false_positives": int(metrics["false_positives"]),
        "false_negatives": int(metrics["false_negatives"]),
        "prohibited_structure_hits": int(metrics["prohibited_structure_hits"]),
    }


def _payloads(candidate: dict[str, Any]) -> list[dict[str, Any]]:
    result: list[dict[str, Any]] = []
    for kind, relative, expected in candidate.get("payloads", ()):
        path = REPO_ROOT / relative
        if path.is_file() and _sha256(path) != expected:
            raise RuntimeError(f"Optional payload checksum mismatch: {path}")
        result.append({"kind": kind, "path": relative, "sha256": expected})
    if "payload_path" in candidate:
        path = REPO_ROOT / candidate["payload_path"]
        if path.is_file() and _sha256(path) != candidate["payload_sha256"]:
            raise RuntimeError(f"Optional payload checksum mismatch: {path}")
        result.append({"kind": "onnx", "path": candidate["payload_path"], "sha256": candidate["payload_sha256"]})
    return result


def _validate_optional_marker_report(candidate: dict[str, Any]) -> None:
    if "aggregate" not in candidate:
        return
    optional = REPO_ROOT / candidate["optional_evidence_path"]
    if not optional.is_file():
        return
    report = _read(optional)
    scenes = report["per_scene"]
    actual = {
        "scene_count": int(report["scene_count"]),
        "exact_scene_count": sum(bool(scene["exact_count"]) for scene in scenes),
        "true_positives": sum(int(scene["metrics_5px"]["true_positives"]) for scene in scenes),
        "false_positives": sum(int(scene["metrics_5px"]["false_positives"]) for scene in scenes),
        "false_negatives": sum(int(scene["metrics_5px"]["false_negatives"]) for scene in scenes),
        "prohibited_structure_hits": sum(
            sum(int(value) for value in scene["hard_negative_hits"].values()) for scene in scenes
        ),
    }
    for key, expected in candidate["aggregate"].items():
        if actual[key] != int(expected):
            raise RuntimeError(f"Optional marker report aggregate mismatch: {candidate['revision']}:{key}")


def _extract_selected_threshold(record: dict[str, Any], candidate: dict[str, Any]) -> float:
    source_path = candidate.get("threshold_source_path")
    if source_path is not None:
        path = REPO_ROOT / source_path
        if _sha256(path) != candidate["threshold_source_sha256"]:
            raise RuntimeError(f"Selected-threshold source checksum mismatch: {path}")
        source = _read(path)
        field_path = candidate.get("threshold_source_field_path")
        field = candidate.get("threshold_source_field")
        if field_path is None and field is None:
            raise RuntimeError(f"Selected threshold source is absent: {candidate['revision']}")
        for key in field_path or (field,):
            if key not in source:
                raise RuntimeError(f"Selected threshold is absent from source: {candidate['revision']}")
            source = source[key]
        source_threshold = float(source)
    else:
        field_path = candidate.get("threshold_source_field_path")
        field = candidate.get("threshold_source_field")
        if field_path is None and field is None:
            raise RuntimeError(f"Selected threshold source is absent: {candidate['revision']}")
        source = record
        for key in field_path or (field,):
            if key not in source:
                raise RuntimeError(f"Selected threshold is absent from aggregate result: {candidate['revision']}")
            source = source[key]
        source_threshold = float(source)
    declared = float(candidate["selected_threshold"])
    if source_threshold != declared:
        raise RuntimeError(
            f"Selected threshold mismatch for {candidate['revision']}: source={source_threshold} declared={declared}"
        )
    return source_threshold


def _validate_optional_approved_classifier_evidence() -> None:
    evidence = REPO_ROOT / APPROVED_MARKER_CLASSIFIER["evidence_path"]
    if evidence.is_file():
        if _sha256(evidence) != APPROVED_MARKER_CLASSIFIER["evidence_file_sha256"]:
            raise RuntimeError("Approved marker classifier evidence checksum mismatch")
        record = _read(evidence)
        public = record["scientific_gates"]["public_v3"]
        if public["report_sha256"] != APPROVED_MARKER_CLASSIFIER["public_v3_report_sha256"]:
            raise RuntimeError("Approved marker classifier public-v3 evidence identity mismatch")
        if record["payload"]["sha256"] != APPROVED_MARKER_CLASSIFIER["payload_sha256"]:
            raise RuntimeError("Approved marker classifier payload identity mismatch")
    report = REPO_ROOT / APPROVED_MARKER_CLASSIFIER["public_v3_report_path"]
    if report.is_file():
        if _sha256(report) != APPROVED_MARKER_CLASSIFIER["public_v3_report_sha256"]:
            raise RuntimeError("Approved marker classifier public-v3 report checksum mismatch")
        public = _read(report)
        if public["metrics"]["shape"]["accuracy"] != APPROVED_MARKER_CLASSIFIER["shape_accuracy"]:
            raise RuntimeError("Approved marker classifier shape accuracy mismatch")
        if public["metrics"]["fill"]["accuracy"] != APPROVED_MARKER_CLASSIFIER["fill_accuracy"]:
            raise RuntimeError("Approved marker classifier fill accuracy mismatch")
        if public["probability_packed_onnx_sha256"] != APPROVED_MARKER_CLASSIFIER["payload_sha256"]:
            raise RuntimeError("Approved marker classifier public-v3 payload identity mismatch")
    payload = REPO_ROOT / APPROVED_MARKER_CLASSIFIER["optional_payload_path"]
    if payload.is_file() and _sha256(payload) != APPROVED_MARKER_CLASSIFIER["payload_sha256"]:
        raise RuntimeError("Approved marker classifier payload checksum mismatch")


def _score(candidate: dict[str, Any], bars: dict[str, Any]) -> dict[str, Any]:
    path = REPO_ROOT / candidate["result_path"]
    record = _read(path)
    ledger_entry: dict[str, Any] | None = None
    if "ledger_revision" in candidate:
        ledger_entry = next(
            item for item in record["revisions"]
            if item["revision"] == candidate["ledger_revision"]
        )
        if _canonical_sha256(ledger_entry) != candidate["ledger_entry_sha256"]:
            raise RuntimeError(f"Aggregate evidence checksum mismatch: {path}")
    elif _sha256(path) != candidate["result_sha256"]:
        raise RuntimeError(f"Aggregate evidence checksum mismatch: {path}")
    if "protocol_path" in candidate:
        protocol = REPO_ROOT / candidate["protocol_path"]
        if _sha256(protocol) != candidate["protocol_sha256"]:
            raise RuntimeError(f"Protocol checksum mismatch: {protocol}")
    if "adapter_factory_path" in candidate:
        factory = REPO_ROOT / candidate["adapter_factory_path"]
        if _sha256(factory) != candidate["adapter_factory_sha256"]:
            raise RuntimeError(f"Adapter factory checksum mismatch: {factory}")
    threshold_record = record
    if ledger_entry is not None:
        threshold_record = ledger_entry
    selected_threshold = _extract_selected_threshold(threshold_record, candidate)
    if "optional_evidence_path" in candidate:
        optional = REPO_ROOT / candidate["optional_evidence_path"]
        if optional.is_file() and _sha256(optional) != candidate["optional_evidence_sha256"]:
            raise RuntimeError(f"Optional aggregate report checksum mismatch: {optional}")
        _validate_optional_marker_report(candidate)
    metrics = _metric(record, candidate)
    detected = metrics["true_positives"] + metrics["false_positives"]
    precision = metrics["true_positives"] / detected if detected else 0.0
    recall = metrics["true_positives"] / (metrics["true_positives"] + metrics["false_negatives"])
    prohibited_rate = metrics["prohibited_structure_hits"] / detected if detected else 0.0
    if candidate["task"] == "marker-center":
        gates = {
            "marker_center_precision": precision >= bars["marker_center_precision_minimum"],
            "marker_center_recall": recall >= bars["marker_center_recall_minimum"],
            "prohibited_structure_hit_rate": prohibited_rate <= bars["prohibited_structure_hit_rate_maximum"],
        }
        return {
            "task": candidate["task"], "name": candidate["name"], "revision": candidate["revision"], "candidate_id": candidate["candidate_id"], "evidence_split": candidate["evidence_split"],
            "result_path": candidate["result_path"], "ledger_entry_sha256": candidate["ledger_entry_sha256"], "selected_threshold": selected_threshold,
            **({"protocol_path": candidate["protocol_path"], "protocol_sha256": candidate["protocol_sha256"], "adapter_factory_path": candidate["adapter_factory_path"], "adapter_factory_sha256": candidate["adapter_factory_sha256"], "operating_configuration": candidate["operating_configuration"]} if "protocol_path" in candidate else {}),
            "metrics": {**metrics, "detected_region_count": detected, "marker_center_precision": precision, "marker_center_recall": recall, "prohibited_structure_hit_rate": prohibited_rate},
            "gates": gates, "tier1_passed": all(gates.values()), "payloads": _payloads(candidate), "payload_available_at_phase3_run": candidate.get("payload_available_at_phase3_run", True), "payload_identity_recorded": bool(candidate.get("payloads") or candidate.get("payload_path")), "payload_reason": candidate.get("payload_reason"),
        }
    recognition_exact = float(record.get("recognition_exact", record.get("selection_metrics", {}).get("recognition_exact", 0.0)))
    cer = float(record.get("character_error_rate", record.get("metrics", {}).get("character_error_rate", 0.0)))
    role = float(record.get("role_accuracy", record.get("metrics", {}).get("role_accuracy", 0.0)))
    if "metrics_path" in candidate:
        nested: dict[str, Any] = record
        for key in candidate["metrics_path"]:
            nested = nested[key]
        recognition_exact = float(nested.get("recognition_exact", nested.get("recognition_exact_match", recognition_exact)))
        cer = float(nested.get("character_error_rate", cer))
        role = float(nested.get("role_accuracy", role))
    gates = {
        "detection_precision": precision >= bars["text_region_detection_precision_minimum"],
        "detection_recall": recall >= bars["text_region_detection_recall_minimum"],
        "recognition_exact_match": recognition_exact >= bars["recognition_exact_match_minimum"],
        "character_error_rate": cer <= bars["character_error_rate_maximum"],
        "role_accuracy": role >= bars["role_accuracy_minimum"],
        "prohibited_structure_hit_rate": prohibited_rate <= bars["prohibited_structure_hit_rate_maximum"],
    }
    return {
        "task": candidate["task"], "name": candidate["name"], "revision": candidate["revision"], "candidate_id": candidate["candidate_id"], "evidence_split": candidate["evidence_split"],
        "result_path": candidate["result_path"], "result_sha256": candidate["result_sha256"], "selected_threshold": selected_threshold,
        **({"protocol_path": candidate["protocol_path"], "protocol_sha256": candidate["protocol_sha256"], "adapter_factory_path": candidate["adapter_factory_path"], "adapter_factory_sha256": candidate["adapter_factory_sha256"], "operating_configuration": candidate["operating_configuration"]} if "protocol_path" in candidate else {}),
        "metrics": {**metrics, "detected_region_count": detected, "detection_precision": precision, "detection_recall": recall, "prohibited_structure_hit_rate": prohibited_rate, "recognition_exact_match": recognition_exact, "character_error_rate": cer, "role_accuracy": role},
        "gates": gates, "tier1_passed": all(gates.values()), "payloads": _payloads(candidate), "payload_available_at_phase3_run": candidate.get("payload_available_at_phase3_run", True), "payload_identity_recorded": bool(candidate.get("payloads") or candidate.get("payload_path")), "payload_reason": candidate.get("payload_reason"),
    }


def rescore() -> dict[str, Any]:
    policy = _read(POLICY_PATH)
    bars = policy["tier1_reviewable_error"]
    _validate_optional_approved_classifier_evidence()
    ocr = [_score(candidate, bars) for candidate in OCR_CANDIDATES]
    markers = [_score(candidate, bars) for candidate in MARKER_CANDIDATES]
    selected_ocr = next(item for item in ocr if item["revision"] == "graphreader-v10-bounded-zero-consensus-ambiguity-alias-composition-v8")
    selected_marker = next(item for item in markers if item["revision"] == "marker-center-runtime-consistency-v2")
    classifier_gates = {
        "marker_shape_accuracy": APPROVED_MARKER_CLASSIFIER["shape_accuracy"] >= bars["marker_shape_accuracy_minimum"],
        "marker_fill_accuracy": APPROVED_MARKER_CLASSIFIER["fill_accuracy"] >= bars["marker_fill_accuracy_minimum"],
    }
    classifier_compatibility_gates = {
        "marker_shape_accuracy": APPROVED_MARKER_CLASSIFIER["shape_accuracy"] >= 0.90,
        "marker_fill_accuracy": APPROVED_MARKER_CLASSIFIER["fill_accuracy"] >= 0.90,
    }
    return {
        "schema_version": 1, "policy_path": POLICY_PATH.relative_to(REPO_ROOT).as_posix(), "policy_sha256": _sha256(POLICY_PATH),
        "evaluation_mode": "recorded_aggregate_metrics_only", "model_training_runs": 0, "model_inference_runs": 0, "sealed_split_reads": 0, "case_level_reads": 0,
        "ocr_candidates": ocr, "marker_candidates": markers,
        "approved_marker_classifier": {
            **APPROVED_MARKER_CLASSIFIER,
            "gates": classifier_gates,
            "approved_payload_compatibility_gates": classifier_compatibility_gates,
            "approved_payload_compatible": all(classifier_compatibility_gates.values()),
            "tier1_compatible": all(classifier_gates.values()),
            "compatibility_finding": "Resolved under AGENTS Section 7.4: the approved payload retains its 0.90 fill gate and 0.95 applies to future candidates.",
        },
        "selected_ocr": {
            "revision": selected_ocr["revision"],
            "candidate_id": selected_ocr["candidate_id"],
            "recorded_public_tier1_passed": selected_ocr["tier1_passed"],
            "corrected_synthetic_tier1_passed": False,
        },
        "selected_marker": {
            "revision": selected_marker["revision"],
            "candidate_id": selected_marker["candidate_id"],
            "recorded_public_tier1_passed": selected_marker["tier1_passed"],
            "real_dev_tier1_passed": False,
            "adapter_path": "src/GraphReader.App/Integration/Workflow/ProductionProposalMarkerCenterAdapter.cs",
            "adapter_sha256": _sha256(MARKER_ADAPTER_PATH),
            "adapter_test_path": "tests/GraphReader.App.Tests/ProductionProposalMarkerCenterAdapterTests.cs",
            "adapter_test_sha256": _sha256(MARKER_ADAPTER_TEST_PATH),
        },
        "selected_adapter_compatibility": {"ocr": True, "marker": True},
        "recorded_public_detection_candidates_clear_tier1": selected_ocr["tier1_passed"] and selected_marker["tier1_passed"],
        "selected_detection_candidates_clear_current_tier1": False,
        "tier1_automatic_pipeline_complete": False,
        "synthetic_candidate_approval": False,
        "private_acceptance": False,
        "real_acceptance_corpus": {
            "study_count": 40,
            "dig_project_count": 171,
            "digitized_point_count": 3055,
            "split_status": "frozen_study_level_assignment",
            "assignment_sha256": "decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a",
            "real_dev_project_count": 120,
            "real_sealed_project_count": 51,
            "real_dev_ocr_axis_scored": True,
            "real_dev_ocr_axis_passed": False,
            "real_dev_marker_center_scored": True,
            "real_dev_marker_center_passed": False,
            "real_sealed_scored": False,
        },
        "post_rescore_evidence": {
            "corrected_ocr_diagnostic_path": "docs/GOAL-22-PHASE-4-CORRECTED-OCR-DIAGNOSTIC.json",
            "corrected_ocr_diagnostic_sha256": "53e6c458f0a9688aca51595b37038811639b798eb2435e8ce653eb8501c74569",
            "real_dev_marker_diagnostic_path": "docs/GOAL-22-PHASE-4-REAL-DEV-MARKER-DIAGNOSTIC.json",
            "real_dev_marker_diagnostic_sha256": "ac87592cf92a3391d59a431d4fa6788042b404beb677ffcca789bf3984c97ac3",
            "real_sealed_reads": 0,
        },
        "manifest_created": False, "model_store_promoted": False, "packaging_discovery": False, "production_approval": False, "release_eligible": False,
        "promotion_blockers": [
            "selected_ocr_fails_corrected_synthetic_tier1",
            "selected_marker_fails_real_dev_tier1",
            "real_sealed_acceptance_not_scored",
            "production_manifest_store_and_package_contract_not_satisfied",
        ],
    }


if __name__ == "__main__":
    import argparse

    parser = argparse.ArgumentParser()
    parser.add_argument("--write", action="store_true")
    arguments = parser.parse_args()
    generated = rescore()
    if arguments.write:
        with RESULT_PATH.open("w", encoding="utf-8", newline="\n") as result_file:
            result_file.write(json.dumps(generated, indent=2, sort_keys=True) + "\n")
    print(json.dumps(generated, indent=2, sort_keys=True))
