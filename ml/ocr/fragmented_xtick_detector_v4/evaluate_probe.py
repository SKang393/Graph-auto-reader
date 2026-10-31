# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Evaluate V4 on the already-consumed procedural probe using RX 6600 XT."""

from __future__ import annotations

from datetime import datetime, timezone
from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path
import sys
from typing import Any

ROOT = Path(__file__).resolve().parent
REPO_ROOT = ROOT.parents[2]
sys.path.insert(0, str(REPO_ROOT))
from ml.ocr.fragmented_xtick_detector_v3.evaluate_p2 import decode_prediction_box, summarize_image_metrics
from ml.ocr.fragmented_xtick_detector_v3.evaluate_probe import _read_probe
from ml.ocr.fragmented_xtick_detector_v3.gpu_train import GPU_PYTHON, build_spatial_locator, require_rx_6600_xt, target_assignments
from ml.ocr.fragmented_xtick_detector_v3.probe_materialize import LABELS as PROBE_LABELS
from ml.ocr.fragmented_xtick_detector_v4.materialize import LABELS as V4_LABELS, P4_TRAIN_LABELS, canonical_json

P4_ROOT = REPO_ROOT / "artifacts/fragmented-xtick-detector-v3-p4-rf31-logwh-stratified-30epoch-v1"
P4_MODEL_ID = "fragmented-xtick-spatial-locator-v3-cell-relative-rf31-logwh"
RUN_RELATIVE = "artifacts/fragmented-xtick-v4-p1-diverse-train-v1"
PROBE_EVAL_RELATIVE = "artifacts/fragmented-xtick-detector-v3-fresh-probe-v1/p4-evaluation-v2.json"


def digest(data: bytes) -> str:
    return sha256(data).hexdigest()


def evaluate(run_relative: str = RUN_RELATIVE, expected_model_identity: str = "fragmented-xtick-spatial-locator-v4-cell-relative-rf31-logwh-diverse") -> dict[str, Any]:
    if Path(sys.executable).resolve() != GPU_PYTHON.resolve():
        raise RuntimeError("V4 probe inference requires the project GPU launcher")
    import torch

    runtime = require_rx_6600_xt(torch)
    device = torch.device(runtime["device"])
    run_root = REPO_ROOT / run_relative
    report_bytes, checkpoint_bytes = (run_root / "report.json").read_bytes(), (run_root / "checkpoint.pt").read_bytes()
    report = json.loads(report_bytes)
    checkpoint_sha = digest(checkpoint_bytes)
    if report.get("checkpoint_sha256") != checkpoint_sha or report.get("model_identity") != expected_model_identity:
        raise RuntimeError("V4 checkpoint does not match its own report/model identity")
    model = build_spatial_locator(torch).to(device)
    payload = torch.load(BytesIO(checkpoint_bytes), map_location=device, weights_only=True)
    if (payload.get("model_id") != report["model_identity"] or payload.get("recipe_id") != report["recipe_id"]
            or payload.get("input_manifest_sha256") != report["train_manifest_sha256"]):
        raise RuntimeError("checkpoint metadata is not bound to V4 report and train manifest")
    model.load_state_dict(payload["state_dict"]); model.eval()
    if any(parameter.device != device for parameter in model.parameters()):
        raise RuntimeError("V4 model is not resident on the RX 6600 XT")
    manifest, images, targets = _read_probe(torch)
    train_manifest = json.loads((REPO_ROOT / "artifacts/fragmented-xtick-v4-diverse-train-v1/manifest.json").read_bytes())
    train_rows, probe_rows = train_manifest["records"], manifest["records"]
    train_ids, probe_ids = {row["case_id"] for row in train_rows}, {row["case_id"] for row in probe_rows}
    train_hashes, probe_hashes = {row["image_sha256"] for row in train_rows}, {row["image_sha256"] for row in probe_rows}
    case_overlap, image_hash_overlap = train_ids & probe_ids, train_hashes & probe_hashes
    label_overlap = set(V4_LABELS) & set(PROBE_LABELS)
    if case_overlap or image_hash_overlap or label_overlap or set(V4_LABELS) & set(P4_TRAIN_LABELS):
        raise RuntimeError("V4 train/probe identity, image hash, or label sets overlap")
    features = torch.stack(images).unsqueeze(1).to(device)
    with torch.inference_mode():
        logits, box_values = model(features)
        scores = logits.sigmoid().squeeze(1).reshape(len(images), -1).detach().cpu().tolist()
        boxes = box_values.detach().cpu().permute(0, 2, 3, 1).tolist()
    rows = []
    assignments = target_assignments(tuple(targets))
    for index, target in enumerate(targets):
        candidates = []
        for cell, score in enumerate(scores[index]):
            row, column = divmod(cell, 32)
            candidates.append((float(score), decode_prediction_box(boxes[index][row][column], row, column)))
        target_cell = None
        if assignments[index] is not None:
            row, column, _ = assignments[index]
            values = tuple(float(value) for value in boxes[index][row][column])
            target_cell = {"score": float(scores[index][row * 32 + column]),
                           "box": decode_prediction_box(values, row, column), "values": values}
        rows.append({"target": target, "target_cell": target_cell, "candidates": candidates})
    probe_eval_path = REPO_ROOT / PROBE_EVAL_RELATIVE
    p4_eval_path = P4_ROOT / "report.json"
    p4_report = json.loads(p4_eval_path.read_bytes())
    old_probe_eval = json.loads(probe_eval_path.read_bytes())
    if p4_report.get("model") != P4_MODEL_ID or old_probe_eval.get("checkpoint_model_id") != P4_MODEL_ID:
        raise RuntimeError("P4 baseline identity does not match the recorded P4 diagnostic")
    return {
        "schema": "graphreader.ocr-fragmented-xtick-v4-consumed-probe-evaluation.v1",
        "status": "consumed-development-diagnostic-not-untouched-acceptance",
        "timestamp_utc": datetime.now(timezone.utc).isoformat(),
        "evaluator_sha256": digest(Path(__file__).read_bytes()),
        "evaluated_model_identity": report["model_identity"], "evaluated_checkpoint_sha256": checkpoint_sha,
        "evaluated_train_manifest_sha256": report["train_manifest_sha256"],
        "baseline_model_identity": P4_MODEL_ID, "baseline_checkpoint_sha256": p4_report["checkpoint_sha256"],
        "baseline_evaluation_sha256": digest(probe_eval_path.read_bytes()),
        "baseline_metrics": old_probe_eval["metrics"], "probe_id": manifest["probe_id"],
        "probe_manifest_sha256": digest((REPO_ROOT / "artifacts/fragmented-xtick-detector-v3-fresh-probe-v1/manifest.json").read_bytes()),
        "train_probe_separation": {"train_case_count": len(train_rows), "probe_case_count": len(probe_rows),
            "case_id_overlap_count": len(case_overlap), "image_sha256_overlap_count": len(image_hash_overlap),
            "train_probe_label_overlap_count": len(label_overlap),
            "v4_p4_train_label_overlap_count": len(set(V4_LABELS) & set(P4_TRAIN_LABELS)),
            "train_records_all_split_train": all(row["split"] == "train" for row in train_rows)},
        "runtime": runtime, "probe_consumed_by_training": False,
        "probe_case_count": len(targets), "probe_positive_count": sum(t is not None for t in targets),
        "probe_background_only_count": sum(t is None for t in targets),
        "metrics": summarize_image_metrics(rows),
        "threshold_contract": {"score_thresholds": [0.5, 0.75, 0.9], "nms_iou_threshold": 0.5, "true_match_iou_threshold": 0.5},
        "formal_acceptance_set": False,
        "distribution_overlap_and_limits": manifest["distribution_overlap_and_limits"],
    }


def main() -> int:
    result = evaluate()
    output = REPO_ROOT / RUN_RELATIVE / "consumed-probe-evaluation-v3.json"
    with output.open("xb") as stream:
        stream.write(canonical_json(result))
    print(json.dumps({"output": output.relative_to(REPO_ROOT).as_posix(), "sha256": digest(output.read_bytes()),
        "v4_checkpoint_sha256": result["evaluated_checkpoint_sha256"], "p4_baseline_checkpoint_sha256": result["baseline_checkpoint_sha256"],
        "metrics": result["metrics"], "runtime": result["runtime"]}, indent=2, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
