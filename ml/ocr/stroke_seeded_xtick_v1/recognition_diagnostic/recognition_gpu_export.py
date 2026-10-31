# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Export pinned P2 top-one boxes on the already-consumed procedural probe."""

from __future__ import annotations

from datetime import datetime, timezone
from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path
import sys
from typing import Any

import numpy as np

ROOT = Path(__file__).resolve().parents[4]
sys.path.insert(0, str(ROOT))
from ml.ocr.fragmented_xtick_detector_v3 import gpu_train as helpers
from ml.ocr.stroke_seeded_xtick_v1 import probe_evaluate_gpu, top_one_seed_analysis, train_gpu

CONFIG_PATH = ROOT / "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/recognition.json"
_PHASE = "starting"


class RecognitionDiagnosticError(RuntimeError):
    pass


def digest(data: bytes) -> str:
    return sha256(data).hexdigest()


def canonical_json(value: object) -> bytes:
    return json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(",", ":")).encode("utf-8")


def load_config() -> tuple[dict[str, Any], bytes]:
    bound = helpers._bound_read(ROOT, "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/recognition.json",
                                "recognition diagnostic config", 65_536)
    config = helpers._parse_json(bound, "recognition diagnostic config")
    expected = {
        "schema": "graphreader.stroke-seeded-xtick-v1-p2-v5-recognition-config.v1",
        "recipe_id": "stroke-seeded-xtick-v1-p2-v5-top-one-recognition-probe-v1",
        "p2_analyzer_config_path": "ml/ocr/stroke_seeded_xtick_v1/top_one_seed_p2.json",
        "p2_analyzer_config_sha256": "055c3cf76c1aee90d031e6352ab218066dc3303ace682972016c75654e4dad6d",
        "p2_analyzer_source_sha256": "732650575a1878e380387c4a066b6e3c5b80755d61293e5f1aafd711bed8254d",
        "p2_top_one_report_path": "artifacts/stroke-seeded-xtick-v1-p2-top-one-seed-analysis-v1/report.json",
        "p2_top_one_report_sha256": "66ba6032cdd9ef66fb14ac8b810bbb3de5e79422dbf9eaca04b096e8cacab970",
        "probe_manifest_sha256": "127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba",
        "probe_eval_report_sha256": "97932d6f2b12debdd009014d2cbcfd5ff63db8754ccc664079df2551d7242340",
        "p2_checkpoint_sha256": "9b0e52ab0ebde0faef51326441d6c01e38f1d94c0a017a5530c75a5902c10351",
        "p2_training_report_sha256": "cc013b5eda70af9681dbd87cc3e496a668cc29d0dcfba8d0841250782bdcb615",
        "p2_training_config_sha256": "1ae28371439c7106631337c12db8f1661f7152778532628db8b3163c6b4cd5e5",
        "p2_runner_sha256": "dfa9fc50455c9102198084062a4a0f391a4a2b674697a26a87abeb2bb30ab9d6",
        "detector_identity": "stroke-seeded-xtick-spatial-locator-rf31-logwh-hard-negative-p2",
        "v5_recognizer_identity": "graph-numeric-component-ensemble-v5",
        "v5_recognizer_onnx_path": "ml/ocr/component_ensemble_v5/artifacts/P1-run/graph-numeric-component-ensemble-v5-p1.onnx",
        "v5_recognizer_onnx_sha256": "9db95c41ce396e8b2dff3b525556615528a00ca87f4cc531274374b961417c84",
        "v5_training_protocol_sha256": "3c4a169eb1b51e6895152c00e0ef2ff664e8154abc911a21cbcba17e0346adfa",
        "v5_alphabet": "0123456789.-%",
        "score_thresholds": [0.25, 0.5],
        "match_iou_threshold": 0.5,
        "probe_input_allowed": True,
        "dev_input_allowed": False,
        "sealed_input_allowed": False,
        "private_input_allowed": False,
        "training_performed": False,
        "planned_output_root": "artifacts/stroke-seeded-xtick-v1-p2-v5-recognition-probe-v1",
        "acceptance_status": "consumed synthetic diagnostic only",
    }
    for key, value in expected.items():
        if config.get(key) != value:
            raise RecognitionDiagnosticError(f"recognition config mismatch: {key}")
    if digest(Path(__file__).read_bytes()) != config.get("expected_exporter_sha256"):
        raise RecognitionDiagnosticError("GPU exporter source hash mismatch")
    return config, bound.value


def run() -> dict[str, Any]:
    global _PHASE
    config, config_bytes = load_config()
    if Path(sys.executable).resolve() != helpers.GPU_PYTHON.resolve():
        raise RecognitionDiagnosticError("frozen P2 inference must use project GPU Python launcher")
    import torch

    _PHASE = "rx6600xt-binding"
    runtime = helpers.require_rx_6600_xt(torch)
    device = torch.device(str(runtime["device"]))

    _PHASE = "pinned-source-admission"
    analyzer_config_bound = helpers._bound_read(ROOT, config["p2_analyzer_config_path"], "P2 analysis config", 65_536)
    analyzer_source_bound = helpers._bound_read(ROOT, "ml/ocr/stroke_seeded_xtick_v1/top_one_seed_analysis.py",
                                                "P2 analysis source", 1_000_000)
    previous_report_bound = helpers._bound_read(ROOT, config["p2_top_one_report_path"], "prior top-one report", 2_000_000)
    if (analyzer_config_bound.sha256 != config["p2_analyzer_config_sha256"]
            or analyzer_source_bound.sha256 != config["p2_analyzer_source_sha256"]
            or previous_report_bound.sha256 != config["p2_top_one_report_sha256"]):
        raise RecognitionDiagnosticError("P2 analysis source/config/report hash changed")
    previous_report = helpers._parse_json(previous_report_bound, "prior top-one report")
    probe_config, _ = probe_evaluate_gpu.load_config()
    if probe_config["probe_manifest_sha256"] != config["probe_manifest_sha256"]:
        raise RecognitionDiagnosticError("consumed probe manifest identity changed")
    probe = probe_evaluate_gpu.load_probe_rows(probe_config)
    training_cfg, training_cfg_bytes = train_gpu.load_config(
        ROOT / "ml/ocr/stroke_seeded_xtick_v1/training/p2.json")
    if digest(training_cfg_bytes) != config["p2_training_config_sha256"]:
        raise RecognitionDiagnosticError("P2 training config hash changed")
    checkpoint_bound = helpers._bound_read(ROOT, "artifacts/stroke-seeded-xtick-v1-p2-hard-negative-fit-v1/checkpoint.pt",
                                           "P2 checkpoint", 1_000_000)
    train_report_bound = helpers._bound_read(ROOT, "artifacts/stroke-seeded-xtick-v1-p2-hard-negative-fit-v1/report.json",
                                             "P2 training report", 1_000_000)
    if checkpoint_bound.sha256 != config["p2_checkpoint_sha256"] or train_report_bound.sha256 != config["p2_training_report_sha256"]:
        raise RecognitionDiagnosticError("P2 checkpoint/report hash changed")
    train_report = helpers._parse_json(train_report_bound, "P2 training report")
    if (train_report.get("trainer_sha256") != config["p2_runner_sha256"]
            or train_report.get("config_sha256") != config["p2_training_config_sha256"]
            or train_report.get("checkpoint_sha256") != checkpoint_bound.sha256
            or train_report.get("runtime", {}).get("device_name") != "AMD Radeon RX 6600 XT"):
        raise RecognitionDiagnosticError("P2 training provenance does not match frozen config")
    model_bound = helpers._bound_read(ROOT, config["v5_recognizer_onnx_path"], "V5 recognizer ONNX", 10_000_000)
    if model_bound.sha256 != config["v5_recognizer_onnx_sha256"]:
        raise RecognitionDiagnosticError("V5 ONNX model checksum mismatch")

    _PHASE = "p2-gpu-inference"
    checkpoint = torch.load(BytesIO(checkpoint_bound.value), map_location=device, weights_only=True)
    expected_checkpoint = {"recipe_id": "stroke-seeded-xtick-v1-hard-negative-fit-p2",
                           "model_identity": config["detector_identity"],
                           "input_manifest_sha256": train_report.get("input_manifest_sha256"),
                           "optimizer_steps": 2760, "model_seed": 27103151,
                           "initialization": "fresh random initialization, no checkpoint loaded"}
    for key, value in expected_checkpoint.items():
        if checkpoint.get(key) != value:
            raise RecognitionDiagnosticError(f"P2 checkpoint metadata mismatch: {key}")
    detector = helpers.build_spatial_locator(torch).to(device)
    detector.load_state_dict(checkpoint["state_dict"], strict=True)
    if any(parameter.device != device for parameter in detector.parameters()):
        raise RecognitionDiagnosticError("P2 detector not resident on RX 6600 XT")
    inputs = probe["pixels"]
    predictions = top_one_seed_analysis._infer(detector, inputs, device, torch)
    targets = probe["boxes"]
    metrics = top_one_seed_analysis.top_one_metrics(predictions, targets, config["score_thresholds"],
                                                     config["match_iou_threshold"])
    expected_metrics = previous_report["consumed_probe_top_one_per_seed"]
    for threshold in ("0.25", "0.5"):
        measured = metrics["score_threshold_metrics"][threshold]
        frozen = expected_metrics["score_threshold_metrics"][threshold]
        for key in ("true_positives", "false_positives", "false_negatives"):
            if measured[key] != frozen[key]:
                raise RecognitionDiagnosticError(f"top-one comparison differs at {threshold}:{key}")

    _PHASE = "prediction-record"
    manifest_by_id = {str(row["case_id"]): row for row in probe["manifest"]["records"]}
    records = []
    for selected, target, row_predictions in zip(probe["rows"], targets, predictions, strict=True):
        source_row = manifest_by_id[selected["case_id"]]
        annotation_path = source_row["annotation_path"]
        annotation_file = helpers._bound_read(ROOT / Path(probe_config["probe_manifest_path"]).parent,
                                              annotation_path, "probe annotation", 65_536)
        annotation = helpers._parse_json(annotation_file, "probe annotation")
        best = row_predictions[0] if row_predictions else None
        records.append({
            "case_id": selected["case_id"], "source_id": selected["source_id"], "kind": selected["kind"],
            "image_path": source_row["image_path"], "image_sha256": selected["image_sha256"],
            "annotation_path": annotation_path, "annotation_sha256": source_row["annotation_sha256"],
            "expected_text": selected["expected_label"],
            "target_xyxy_pixels": annotation.get("targets", [{}])[0].get("target_crop_xyxy") if target is not None else None,
            "top_one_score": float(best[0]) if best is not None else None,
            "top_one_xyxy_normalized": [float(value) for value in best[1]] if best is not None else None,
            "selected_at_thresholds": {str(t): bool(best is not None and best[0] >= t)
                                        for t in config["score_thresholds"]},
        })
    return {
        "schema": "graphreader.stroke-seeded-xtick-v1-p2-v5-recognition-predictions.v1",
        "recipe_id": config["recipe_id"], "acceptance_status": config["acceptance_status"],
        "config_sha256": digest(config_bytes), "exporter_sha256": digest(Path(__file__).read_bytes()),
        "runtime": runtime, "detector_identity": config["detector_identity"],
        "detector_checkpoint_sha256": checkpoint_bound.sha256,
        "probe_manifest_sha256": probe["manifest_sha256"],
        "probe_eval_report_sha256": config["probe_eval_report_sha256"],
        "v5_recognizer_identity": config["v5_recognizer_identity"],
        "v5_recognizer_onnx_sha256": model_bound.sha256,
        "v5_training_protocol_sha256": config["v5_training_protocol_sha256"],
        "match_iou_threshold": config["match_iou_threshold"], "score_thresholds": config["score_thresholds"],
        "top_one_metrics": metrics, "record_count": len(records), "records": records,
        "training_performed": False, "dev_sealed_private_inputs_read": False,
        "production_approval": False, "release_eligible": False,
        "finished_utc": datetime.now(timezone.utc).isoformat(),
    }


if __name__ == "__main__":
    output_rel = "artifacts/stroke-seeded-xtick-v1-p2-v5-recognition-probe-v1"
    output = ROOT / output_rel
    if output.exists() or output.is_symlink():
        raise RecognitionDiagnosticError("diagnostic output must be a fresh project artifact path")
    helpers._checked_chain(ROOT, output.parent, "diagnostic output parent")
    output.mkdir(parents=True, exist_ok=False)
    try:
        report = run()
        (output / "predictions.json").write_bytes(canonical_json(report))
        (output / "report.json").write_bytes(canonical_json({key: value for key, value in report.items() if key != "records"}))
        print(json.dumps({key: value for key, value in report.items() if key != "records"}, sort_keys=True, indent=2))
    except Exception as error:
        (output / "failure.json").write_bytes(canonical_json({
            "schema": "graphreader.stroke-seeded-xtick-v1-p2-v5-recognition-failure.v1",
            "status": "failed", "recipe_id": "stroke-seeded-xtick-v1-p2-v5-top-one-recognition-probe-v1",
            "phase": _PHASE, "failure_type": type(error).__name__, "failure_message": str(error),
            "training_performed": False, "dev_sealed_private_inputs_read": False,
        }))
        raise
