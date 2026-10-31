# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""GPU-only image-level evaluation of the registered local P2 train-fit checkpoint."""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path
from statistics import median
import sys
from typing import Any, Sequence

import numpy as np
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parents[3]))
from ml.ocr.fragmented_xtick_detector_v3.gpu_train import (
    GPU_PYTHON,
    REPO_ROOT,
    _annotation_target,
    _bound_read,
    _config,
    _parse_json,
    _sha256_bytes,
    build_spatial_locator,
    decode_cell_relative,
    require_rx_6600_xt,
    validate_train_input,
)


ARTIFACT_ROOT = REPO_ROOT / "artifacts" / "fragmented-xtick-detector-v3-p2-stratified-30epoch-trainfit-v1"
CHECKPOINT_RELATIVE = "artifacts/fragmented-xtick-detector-v3-p2-stratified-30epoch-trainfit-v1/checkpoint.pt"
TRAIN_THRESHOLDS = (0.5, 0.75, 0.9)
NMS_IOU_THRESHOLD = 0.5
MATCH_IOU_THRESHOLD = 0.5


def _clip_box(box: Sequence[float]) -> tuple[float, float, float, float]:
    left, top, right, bottom = (float(value) for value in box)
    return max(0.0, min(1.0, left)), max(0.0, min(1.0, top)), max(0.0, min(1.0, right)), max(0.0, min(1.0, bottom))


def decode_prediction_box(values: Sequence[float], row: int, column: int) -> tuple[float, float, float, float]:
    """Decode one sigmoid cell-relative xy-offset/wh prediction and clip to image."""
    return _clip_box(decode_cell_relative(tuple(float(value) for value in values), row, column))


def box_iou(first: Sequence[float], second: Sequence[float]) -> float:
    left = max(first[0], second[0])
    top = max(first[1], second[1])
    right = min(first[2], second[2])
    bottom = min(first[3], second[3])
    intersection = max(0.0, right - left) * max(0.0, bottom - top)
    area_first = max(0.0, first[2] - first[0]) * max(0.0, first[3] - first[1])
    area_second = max(0.0, second[2] - second[0]) * max(0.0, second[3] - second[1])
    union = area_first + area_second - intersection
    return intersection / union if union > 0 else 0.0


def nms(candidates: Sequence[tuple[float, Sequence[float]]], iou_threshold: float) -> list[tuple[float, tuple[float, float, float, float]]]:
    ordered = sorted(((float(score), _clip_box(box)) for score, box in candidates), key=lambda item: item[0], reverse=True)
    kept: list[tuple[float, tuple[float, float, float, float]]] = []
    while ordered:
        current = ordered.pop(0)
        kept.append(current)
        ordered = [item for item in ordered if box_iou(current[1], item[1]) <= iou_threshold]
    return kept


def summarize_image_metrics(
    images: Sequence[dict[str, Any]],
    thresholds: Sequence[float] = TRAIN_THRESHOLDS,
    nms_iou_threshold: float = NMS_IOU_THRESHOLD,
    match_iou_threshold: float = MATCH_IOU_THRESHOLD,
) -> dict[str, Any]:
    positives = [image for image in images if image["target"] is not None]
    backgrounds = [image for image in images if image["target"] is None]
    if not positives or not backgrounds:
        raise ValueError("evaluation requires positive and background-only images")
    top_ious = [box_iou(max(image["candidates"], key=lambda item: item[0])[1], image["target"]) for image in positives]
    result: dict[str, Any] = {
        "image_count": len(images),
        "positive_image_count": len(positives),
        "background_only_image_count": len(backgrounds),
        "highest_confidence_iou": {
            "mean": sum(top_ious) / len(top_ious),
            "median": median(top_ious),
            "fraction_at_or_above_0_5": sum(iou >= 0.5 for iou in top_ious) / len(top_ious),
            "fraction_at_or_above_0_75": sum(iou >= 0.75 for iou in top_ious) / len(top_ious),
        },
        "thresholds": {},
    }
    for threshold in thresholds:
        by_kind: dict[str, dict[str, float]] = {}
        for label, group in (("positive", positives), ("background_only", backgrounds)):
            raw_counts: list[int] = []
            kept_counts: list[int] = []
            false_counts: list[int] = []
            matched_count = 0
            for image in group:
                raw = [(score, box) for score, box in image["candidates"] if score >= threshold]
                kept = nms(raw, nms_iou_threshold)
                matched = image["target"] is not None and any(box_iou(box, image["target"]) >= match_iou_threshold for _, box in kept)
                raw_counts.append(len(raw))
                kept_counts.append(len(kept))
                false_counts.append(len(kept) - int(matched))
                matched_count += int(matched)
            by_kind[label] = {
                "raw_proposals_per_image": sum(raw_counts) / len(group),
                "nms_detections_per_image": sum(kept_counts) / len(group),
                "false_positives_per_image": sum(false_counts) / len(group),
            }
            if label == "positive":
                by_kind[label]["image_recall"] = matched_count / len(group)
        result["thresholds"][str(threshold)] = {
            "nms_iou_threshold": nms_iou_threshold,
            "match_iou_threshold": match_iou_threshold,
            "positive_images": by_kind["positive"],
            "background_only_images": by_kind["background_only"],
        }
    return result


def _load_train_data(config: dict[str, Any], torch: Any) -> tuple[list[Any], list[tuple[float, float, float, float] | None]]:
    manifest_bound = _bound_read(REPO_ROOT, str(config["train_manifest_path"]), "V3 evaluation train manifest")
    if manifest_bound.sha256 != config["train_manifest_sha256"]:
        raise RuntimeError("evaluation manifest hash differs from P2 config")
    manifest = _parse_json(manifest_bound, "V3 evaluation train manifest")
    root = manifest_bound.path.parent
    tensors: list[Any] = []
    targets: list[tuple[float, float, float, float] | None] = []
    for row in manifest["records"]:
        image = _bound_read(root, str(row["image_path"]), "V3 evaluation train image")
        annotation = _parse_json(_bound_read(root, str(row["annotation_path"]), "V3 evaluation train annotation"), "V3 evaluation train annotation")
        if image.sha256 != row["image_sha256"] or _sha256_bytes(json.dumps(annotation, ensure_ascii=True, sort_keys=True, separators=(",", ":")).encode()) != row["annotation_sha256"]:
            raise RuntimeError("evaluation image or annotation hash differs from the train manifest")
        if annotation.get("image_sha256") != image.sha256 or annotation.get("case_id") != row["case_id"]:
            raise RuntimeError("evaluation annotation does not bind its image")
        array = np.asarray(Image.open(BytesIO(image.value)).convert("L"), dtype=np.float32) / 255.0
        tensors.append(torch.from_numpy(array))
        targets.append(_annotation_target(annotation, positive=row["kind"] == "positive"))
    return tensors, targets


def evaluate() -> dict[str, Any]:
    if Path(sys.executable).resolve() != GPU_PYTHON.resolve():
        raise RuntimeError("P2 evaluation must use the project GPU launcher; CPU inference is forbidden")
    import torch

    config = _config()
    input_receipt = validate_train_input()
    runtime = require_rx_6600_xt(torch)
    device = torch.device(str(runtime["device"]))
    p2_report_path = ARTIFACT_ROOT / "report.json"
    p2_report = json.loads(p2_report_path.read_text(encoding="utf-8"))
    if p2_report.get("input", {}).get("manifest_sha256") != input_receipt["manifest_sha256"]:
        raise RuntimeError("P2 checkpoint report is not bound to the evaluated train manifest")
    checkpoint = _bound_read(REPO_ROOT, CHECKPOINT_RELATIVE, "P2 checkpoint", 10_000_000)
    if checkpoint.sha256 != p2_report.get("checkpoint_sha256"):
        raise RuntimeError("P2 checkpoint hash differs from its training report")
    model = build_spatial_locator(torch).to(device)
    payload = torch.load(BytesIO(checkpoint.value), map_location=device, weights_only=True)
    if payload.get("input_manifest_sha256") != input_receipt["manifest_sha256"]:
        raise RuntimeError("P2 checkpoint does not bind the evaluated train manifest")
    model.load_state_dict(payload["state_dict"])
    model.eval()
    if any(parameter.device != device for parameter in model.parameters()):
        raise RuntimeError("P2 model is not resident on RX 6600 XT")
    tensors, targets = _load_train_data(config, torch)
    features = torch.stack(tensors).unsqueeze(1).to(device)
    if features.device != device:
        raise RuntimeError("P2 evaluation inputs are not resident on RX 6600 XT")
    with torch.inference_mode():
        logits, box_values = model(features)
        scores = logits.sigmoid().squeeze(1).reshape(len(tensors), -1).detach().cpu().tolist()
        boxes = box_values.detach().cpu().permute(0, 2, 3, 1).tolist()
    rows: list[dict[str, Any]] = []
    for index, target in enumerate(targets):
        candidates = []
        for cell, score in enumerate(scores[index]):
            row, column = divmod(cell, 32)
            candidates.append((float(score), decode_prediction_box(boxes[index][row][column], row, column)))
        rows.append({"target": target, "candidates": candidates})
    metrics = summarize_image_metrics(rows)
    return {
        "schema": "graphreader.ocr-fragmented-xtick-v3-image-evaluation.v1",
        "status": "train-fit-diagnostic-only",
        "started_utc": datetime.now(timezone.utc).isoformat(),
        "checkpoint_sha256": checkpoint.sha256,
        "train_manifest_sha256": input_receipt["manifest_sha256"],
        "runtime": runtime,
        "inference_device": str(device),
        "cpu_fallback": False,
        "input_scope": {"train": True, "dev": False, "sealed": False, "private": False},
        "evaluation": metrics,
        "production_approval": False,
        "release_eligible": False,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ARTIFACT_ROOT / "image-evaluation-v2.json")
    args = parser.parse_args()
    result = evaluate()
    output = args.output if args.output.is_absolute() else REPO_ROOT / args.output
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("x", encoding="utf-8") as stream:
        json.dump(result, stream, ensure_ascii=True, sort_keys=True, separators=(",", ":"))
    print(json.dumps({"output": output.relative_to(REPO_ROOT).as_posix(), "result_sha256": sha256(output.read_bytes()).hexdigest(), "evaluation": result["evaluation"], "runtime": result["runtime"]}, indent=2, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
