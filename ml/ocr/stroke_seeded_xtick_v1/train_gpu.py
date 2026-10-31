# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""GPU-only train-fit experiment on the pinned V6 stroke-seeded corpus."""

from __future__ import annotations

from datetime import datetime, timezone
from hashlib import sha256
from io import BytesIO
import json
import math
from pathlib import Path
import sys
from typing import Any, Mapping

from PIL import Image

ROOT = Path(__file__).resolve().parent
REPO_ROOT = ROOT.parents[2]
sys.path.insert(0, str(REPO_ROOT))
from ml.ocr.fragmented_xtick_detector_v3 import gpu_train as helpers


CONFIG = ROOT / "training" / "p1.json"
MANIFEST_RELATIVE = "artifacts/stroke-seeded-xtick-v1-train-verified-v6/manifest.json"
_RUN = {"phase": "starting", "steps": 0}


class StrokeSeededTrainingError(RuntimeError):
    pass


def digest(data: bytes) -> str:
    return sha256(data).hexdigest()


def canonical_json(value: object) -> bytes:
    return json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(",", ":")).encode("utf-8")


def load_config() -> tuple[dict[str, Any], bytes]:
    bound = helpers._bound_read(REPO_ROOT, "ml/ocr/stroke_seeded_xtick_v1/training/p1.json",
                                "stroke-seeded GPU config", 65_536)
    config = helpers._parse_json(bound, "stroke-seeded GPU config")
    expected = {
        "schema": "graphreader.stroke-seeded-xtick-v1-gpu-config.v1",
        "recipe_id": "stroke-seeded-xtick-v1-single-box-fit-p1",
        "train_manifest_path": MANIFEST_RELATIVE,
        "train_manifest_sha256": "12565af18cd69b680b978454058ab0f4448336931e5db7b3c128b861af9e0f1a",
        "train_manifest_record_count": 681,
        "trainable_positive_count": 235,
        "trainable_background_count": 409,
        "excluded_count": 37,
        "source_group_count": 24,
        "canvas": [256, 96],
        "epochs": 30,
        "batch_size": 7,
        "steps_per_epoch": 92,
        "expected_optimizer_steps": 2760,
        "model_seed": 27103151,
        "shuffle_seed": 27103152,
        "score_thresholds": [0.25, 0.5],
        "nms_iou_threshold": 0.5,
        "match_iou_threshold": 0.5,
        "maximum_detections_per_image": 100,
        "optimizer_execution_authorized": True,
        "train_only": True,
        "dev_input_allowed": False,
        "sealed_input_allowed": False,
        "private_input_allowed": False,
        "probe_input_allowed": False,
        "cpu_fallback_allowed": False,
        "planned_output_root": "artifacts/stroke-seeded-xtick-v1-p1-rx6600xt-fit-v1",
        "production_approval": False,
        "release_eligible": False,
    }
    for key, value in expected.items():
        if config.get(key) != value:
            raise StrokeSeededTrainingError(f"training config mismatch: {key}")
    if config.get("expected_runner_sha256") != digest(Path(__file__).read_bytes()):
        raise StrokeSeededTrainingError("stroke-seeded GPU trainer source hash changed")
    return config, bound.value


def load_train_inputs(config: Mapping[str, Any]) -> dict[str, Any]:
    """Bind the manifest bytes, verify every V6 artifact, and admit trainable rows only."""
    manifest_bound = helpers._bound_read(
        REPO_ROOT, str(config["train_manifest_path"]), "pinned V6 training manifest", 5_000_000)
    if manifest_bound.sha256 != config["train_manifest_sha256"]:
        raise StrokeSeededTrainingError("V6 manifest byte hash differs from the pinned recipe")
    manifest = helpers._parse_json(manifest_bound, "V6 training manifest")
    exact = {
        "schema": "graphreader.stroke-seeded-xtick-v1-train.v1", "family": "stroke-seeded-xtick-v1",
        "split": "train", "record_count": 681, "source_count": 24,
        "positive_count": 235, "background_only_count": 409, "excluded_count": 37,
        "probe_dev_sealed_private_inputs_read": False,
    }
    for key, value in exact.items():
        if manifest.get(key) != value:
            raise StrokeSeededTrainingError(f"V6 manifest scope/count mismatch: {key}")
    sources = manifest.get("sources")
    records = manifest.get("records")
    if not isinstance(sources, list) or len(sources) != 24 or not isinstance(records, list) or len(records) != 681:
        raise StrokeSeededTrainingError("V6 manifest inventories are malformed")
    source_by_id: dict[str, str] = {}
    for source in sources:
        if not isinstance(source, dict) or source.get("split") != "train":
            raise StrokeSeededTrainingError("non-train source in V6 manifest")
        source_id, source_hash = source.get("source_id"), source.get("source_identity_sha256")
        recipe = source.get("recipe")
        if not isinstance(source_id, str) or not isinstance(source_hash, str) or not isinstance(recipe, dict):
            raise StrokeSeededTrainingError("V6 source identity is malformed")
        identity_payload = dict(recipe)
        identity_payload.pop("source_identity_sha256", None)
        if digest(canonical_json(identity_payload)) != source_hash or recipe.get("source_id") != source_id:
            raise StrokeSeededTrainingError("V6 source identity does not bind its recipe")
        if source_id in source_by_id:
            raise StrokeSeededTrainingError("duplicate V6 source group ID")
        source_by_id[source_id] = source_hash

    root = manifest_bound.path.parent
    seen_case_ids: set[str] = set()
    included_rows: list[dict[str, Any]] = []
    pixel_rows: list[Any] = []
    target_boxes: list[tuple[float, float, float, float] | None] = []
    group_counts: dict[str, int] = {}
    source_row_counts: dict[str, int] = {}
    source_image_hashes: dict[str, str] = {}
    kind_counts = {"positive": 0, "background_only": 0, "excluded": 0}
    for row in records:
        if not isinstance(row, dict) or row.get("split") != "train":
            raise StrokeSeededTrainingError("V6 record is malformed or outside train")
        case_id, source_id = row.get("case_id"), row.get("source_id")
        if not isinstance(case_id, str) or case_id in seen_case_ids or source_id not in source_by_id:
            raise StrokeSeededTrainingError("V6 case or source group identity is invalid")
        seen_case_ids.add(case_id)
        source_row_counts[source_id] = source_row_counts.get(source_id, 0) + 1
        if row.get("source_identity_sha256") != source_by_id[source_id]:
            raise StrokeSeededTrainingError("V6 row source identity differs from its group")
        kind = row.get("kind")
        if kind not in kind_counts:
            raise StrokeSeededTrainingError("unknown V6 crop kind")
        kind_counts[kind] += 1
        if type(row.get("trainable_single_box")) is not bool:
            raise StrokeSeededTrainingError("V6 trainability flag is missing")
        image_path, annotation_path = row.get("image_path"), row.get("annotation_path")
        if not isinstance(image_path, str) or not isinstance(annotation_path, str):
            raise StrokeSeededTrainingError("V6 artifact paths are invalid")
        image_bound = helpers._bound_read(root, image_path, f"V6 image {case_id}", 1_000_000)
        annotation_bound = helpers._bound_read(root, annotation_path, f"V6 annotation {case_id}", 65_536)
        if image_bound.sha256 != row.get("image_sha256") or annotation_bound.sha256 != row.get("annotation_sha256"):
            raise StrokeSeededTrainingError("V6 image or annotation byte hash differs from manifest")
        annotation = helpers._parse_json(annotation_bound, f"V6 annotation {case_id}")
        for key in ("case_id", "source_id", "source_identity_sha256", "split", "kind", "trainable_single_box", "image_sha256"):
            expected_value = row.get(key)
            if annotation.get(key) != expected_value:
                raise StrokeSeededTrainingError(f"V6 annotation binding mismatch: {case_id}:{key}")
        if annotation.get("image_sha256") != image_bound.sha256:
            raise StrokeSeededTrainingError("V6 annotation does not bind the consumed image")
        source_hash = annotation.get("source_sha256")
        if not isinstance(source_hash, str) or len(source_hash) != 64:
            raise StrokeSeededTrainingError("V6 source image hash is malformed")
        if source_id in source_image_hashes and source_image_hashes[source_id] != source_hash:
            raise StrokeSeededTrainingError("source group maps to multiple full-panel identities")
        source_image_hashes[source_id] = source_hash
        try:
            with Image.open(BytesIO(image_bound.value)) as image_file:
                image = image_file.convert("L")
                if image_file.size != (256, 96) or image_file.mode != "L":
                    raise StrokeSeededTrainingError("V6 crop image mode or dimensions differ")
                pixels = __import__("numpy").asarray(image, dtype=__import__("numpy").float32) / 255.0
        except OSError as error:
            raise StrokeSeededTrainingError(f"V6 image decode failed: {case_id}") from error

        if kind == "excluded":
            if (row["trainable_single_box"] or not isinstance(annotation.get("targets"), list)
                    or not annotation.get("exclusion_reasons")):
                raise StrokeSeededTrainingError("excluded V6 row has invalid admission state")
            continue
        if not row["trainable_single_box"]:
            raise StrokeSeededTrainingError("non-excluded V6 row is not admitted for single-box training")
        targets = annotation.get("targets")
        if kind == "positive":
            if not isinstance(targets, list) or len(targets) != 1:
                raise StrokeSeededTrainingError("trainable V6 positive does not have exactly one target")
            target = targets[0]
            if target.get("text") != annotation.get("expected_tick_label"):
                raise StrokeSeededTrainingError("positive target is not the seeded tick label")
            if annotation.get("exclusion_reasons") or annotation.get("exclusion_reason") is not None:
                raise StrokeSeededTrainingError("trainable V6 positive carries exclusion diagnostics")
            source_window = annotation["crop_transform"]["source_window_xywh"]
            gx0, gy0, gx1, gy1 = target["glyph_mask_xyxy"]
            sx, sy, sw, sh = source_window
            if not (sx <= gx0 < gx1 <= sx + sw and sy <= gy0 < gy1 <= sy + sh):
                raise StrokeSeededTrainingError("trainable positive contains a clipped glyph")
            x0, y0, x1, y1 = (float(value) for value in target["target_crop_xyxy"])
            if not (0 <= x0 < x1 <= 256 and 0 <= y0 < y1 <= 96):
                raise StrokeSeededTrainingError("V6 positive box is invalid in crop pixels")
            transform = annotation["crop_transform"]
            forward, inverse = transform["original_to_crop_pixel"], transform["crop_pixel_to_original"]
            mapped = (gx0 * forward[0] + forward[2], gy0 * forward[4] + forward[5],
                      gx1 * forward[0] + forward[2], gy1 * forward[4] + forward[5])
            if any(abs(a - b) > 1e-6 for a, b in zip(mapped, (x0, y0, x1, y1))):
                raise StrokeSeededTrainingError("positive target differs from its forward crop transform")
            restored = (x0 * inverse[0] + inverse[2], y0 * inverse[4] + inverse[5],
                        x1 * inverse[0] + inverse[2], y1 * inverse[4] + inverse[5])
            if any(abs(a - b) > 1e-6 for a, b in zip(restored, (gx0, gy0, gx1, gy1))):
                raise StrokeSeededTrainingError("positive crop transform is not reversible")
            target_boxes.append((x0 / 256.0, y0 / 96.0, x1 / 256.0, y1 / 96.0))
        else:
            if targets != [] or annotation.get("expected_tick_label") is not None:
                raise StrokeSeededTrainingError("V6 background crop contains or expects text")
            target_boxes.append(None)
        included_rows.append({"case_id": case_id, "source_id": source_id,
                              "source_identity_sha256": source_by_id[source_id], "kind": kind})
        group_counts[source_id] = group_counts.get(source_id, 0) + 1
        pixel_rows.append(pixels)

    if kind_counts != {"positive": 235, "background_only": 409, "excluded": 37}:
        raise StrokeSeededTrainingError(f"V6 row class counts changed: {kind_counts}")
    if len(included_rows) != 644 or len(pixel_rows) != 644 or len(target_boxes) != 644:
        raise StrokeSeededTrainingError("V6 admitted rows differ from the pinned class counts")
    if len(group_counts) != 24 or min(group_counts.values()) < 1:
        raise StrokeSeededTrainingError("V6 trainable rows do not preserve all source groups")
    for source in sources:
        if source_row_counts.get(source["source_id"]) != source.get("crop_count"):
            raise StrokeSeededTrainingError("source group crop count differs from the pinned manifest")
    return {
        "manifest_sha256": manifest_bound.sha256, "manifest_bytes": manifest_bound.value,
        "manifest": manifest, "pixels": pixel_rows, "boxes": target_boxes,
        "rows": included_rows, "group_counts": group_counts, "kind_counts": kind_counts,
        "admitted_count": len(included_rows),
    }


def box_iou(a: tuple[float, float, float, float], b: tuple[float, float, float, float]) -> float:
    ax0, ay0, ax1, ay1 = a
    bx0, by0, bx1, by1 = b
    intersection = max(0.0, min(ax1, bx1) - max(ax0, bx0)) * max(0.0, min(ay1, by1) - max(ay0, by0))
    area_a = max(0.0, ax1 - ax0) * max(0.0, ay1 - ay0)
    area_b = max(0.0, bx1 - bx0) * max(0.0, by1 - by0)
    union = area_a + area_b - intersection
    return intersection / union if union > 0 else 0.0


def decode_predictions(logits: Any, raw_boxes: Any) -> list[list[tuple[float, tuple[float, float, float, float]]]]:
    """Decode all 12x32 cells into score-sorted image-normalized boxes."""
    scores = logits.detach().sigmoid().cpu().numpy()
    boxes = raw_boxes.detach().cpu().numpy()
    decoded: list[list[tuple[float, tuple[float, float, float, float]]]] = []
    for index in range(scores.shape[0]):
        current = []
        for row in range(12):
            for column in range(32):
                value = tuple(float(x) for x in boxes[index, :, row, column])
                box = helpers.decode_cell_relative(value, row, column)
                x0, y0, x1, y1 = (min(1.0, max(0.0, float(v))) for v in box)
                if x1 > x0 and y1 > y0:
                    current.append((float(scores[index, 0, row, column]), (x0, y0, x1, y1)))
        current.sort(key=lambda item: item[0], reverse=True)
        decoded.append(current)
    return decoded


def nms(detections: list[tuple[float, tuple[float, float, float, float]]], threshold: float) -> list[tuple[float, tuple[float, float, float, float]]]:
    kept = []
    for detection in detections:
        if all(box_iou(detection[1], prior[1]) <= threshold for prior in kept):
            kept.append(detection)
    return kept


def image_detection_metrics(predictions: list[list[tuple[float, tuple[float, float, float, float]]]],
                            targets: list[tuple[float, float, float, float] | None],
                            score_thresholds: list[float], nms_threshold: float,
                            match_threshold: float, maximum_detections: int = 100) -> dict[str, Any]:
    if len(predictions) != len(targets):
        raise ValueError("prediction and target counts differ")
    positives = [i for i, target in enumerate(targets) if target is not None]
    backgrounds = [i for i, target in enumerate(targets) if target is None]
    top_ious = [box_iou(predictions[i][0][1], targets[i]) if predictions[i] and targets[i] else 0.0 for i in positives]
    metrics: dict[str, Any] = {
        "image_count": len(targets), "positive_count": len(positives), "background_only_count": len(backgrounds),
        "top_confidence_box_mean_iou_on_positives": sum(top_ious) / max(1, len(top_ious)),
        "top_confidence_box_median_iou_on_positives": sorted(top_ious)[len(top_ious) // 2] if top_ious else 0.0,
        "top_confidence_box_iou_at_0_5_count": sum(value >= match_threshold for value in top_ious),
        "nms_iou_threshold": nms_threshold, "match_iou_threshold": match_threshold,
        "maximum_detections_per_image": maximum_detections,
        "score_threshold_metrics": {},
    }
    for score_threshold in score_thresholds:
        tp = fp = fn = top1_matched = 0
        bg_fp = 0
        for index, truth in enumerate(targets):
            filtered = [d for d in predictions[index] if d[0] >= score_threshold][:maximum_detections]
            detections = nms(filtered, nms_threshold)
            matched_truth = False
            for detection in detections:
                match = truth is not None and not matched_truth and box_iou(detection[1], truth) >= match_threshold
                if match:
                    tp += 1
                    matched_truth = True
                else:
                    fp += 1
                    if truth is None:
                        bg_fp += 1
            if truth is not None and not matched_truth:
                fn += 1
            if truth is not None and predictions[index] and predictions[index][0][0] >= score_threshold and box_iou(predictions[index][0][1], truth) >= match_threshold:
                top1_matched += 1
        precision = tp / max(1, tp + fp)
        recall = tp / max(1, tp + fn)
        metrics["score_threshold_metrics"][str(score_threshold)] = {
            "true_positives": tp, "false_positives": fp, "false_negatives": fn,
            "precision": precision, "recall": recall,
            "false_positives_per_image": fp / max(1, len(targets)),
            "false_positives_per_background_image": bg_fp / max(1, len(backgrounds)),
            "top_confidence_box_recall": top1_matched / max(1, len(positives)),
        }
    return metrics


def _image_metrics(model: Any, features: Any, targets: list[tuple[float, float, float, float] | None],
                   device: Any, torch: Any, config: Mapping[str, Any]) -> dict[str, Any]:
    was_training = model.training
    model.eval()
    with torch.no_grad():
        logits, boxes = model(features.to(device))
        decoded = decode_predictions(logits, boxes)
    if was_training:
        model.train()
    return image_detection_metrics(decoded, targets, list(config["score_thresholds"]),
                                   float(config["nms_iou_threshold"]), float(config["match_iou_threshold"]),
                                   int(config["maximum_detections_per_image"]))


def _run_training(config: Mapping[str, Any], config_bytes: bytes, output: Path) -> dict[str, Any]:
    if Path(sys.executable).resolve() != helpers.GPU_PYTHON.resolve():
        raise StrokeSeededTrainingError("training must run via tools/gpu-training/Run-GpuPython.ps1")
    import numpy as np
    import torch
    from torch import nn

    _RUN["phase"] = "rx6600xt-binding"
    runtime = helpers.require_rx_6600_xt(torch)
    device = torch.device(str(runtime["device"]))
    torch.manual_seed(int(config["model_seed"]))
    np.random.seed(int(config["model_seed"]) % (2**32))

    _RUN["phase"] = "pinned-v6-input-admission"
    admitted = load_train_inputs(config)
    features = torch.from_numpy(np.stack(admitted["pixels"]).astype(np.float32)).unsqueeze(1)
    boxes = tuple(admitted["boxes"])
    assignments = helpers.target_assignments(boxes)
    count = len(boxes)
    objectness = torch.zeros((count, 1, 12, 32), dtype=torch.float32)
    box_targets = torch.zeros((count, 4, 12, 32), dtype=torch.float32)
    positive_mask = torch.zeros((count, 1, 12, 32), dtype=torch.bool)
    for index, assignment in enumerate(assignments):
        if assignment is not None:
            row, column, target = assignment
            objectness[index, 0, row, column] = 1.0
            box_targets[index, :, row, column] = torch.tensor(target, dtype=torch.float32)
            positive_mask[index, 0, row, column] = True

    _RUN["phase"] = "model-construction"
    model = helpers.build_spatial_locator(torch).to(device)
    receptive_field = helpers.feature_receptive_field(model.features, torch)
    if receptive_field != 31 or any(parameter.device != device for parameter in model.parameters()):
        raise StrokeSeededTrainingError("model architecture or RX 6600 XT residency differs")
    with torch.no_grad():
        probe_output = model(features[:1].to(device))
    if list(probe_output[0].shape[-2:]) != [12, 32] or list(probe_output[1].shape[-2:]) != [12, 32]:
        raise StrokeSeededTrainingError("model spatial output shape differs from V6 target grid")

    optimizer = torch.optim.AdamW(model.parameters(), lr=float(config["learning_rate"]))
    before_cells = helpers.evaluate_train_fit(model, features, objectness, box_targets, positive_mask, device, torch)
    before_images = _image_metrics(model, features, boxes, device, torch, config)
    positives = tuple(i for i, box in enumerate(boxes) if box is not None)
    backgrounds = tuple(i for i, box in enumerate(boxes) if box is None)
    batches = helpers.stratified_batches(positives, backgrounds, int(config["batch_size"]), int(config["shuffle_seed"]))
    if len(batches) != int(config["steps_per_epoch"]):
        raise StrokeSeededTrainingError("V6 stratified batch count differs from config")
    losses: list[float] = []
    positive_losses: list[float] = []
    background_losses: list[float] = []
    epoch_reports = []
    batch_class_counts = []
    _RUN["phase"] = "optimizer-training"
    for epoch in range(int(config["epochs"])):
        epoch_batches = helpers.stratified_batches(positives, backgrounds, int(config["batch_size"]), int(config["shuffle_seed"]) + epoch)
        flattened = [index for batch in epoch_batches for index in batch]
        if len(flattened) != count or len(set(flattened)) != count:
            raise StrokeSeededTrainingError("epoch omitted or duplicated a trainable crop")
        epoch_losses = []
        for indices in epoch_batches:
            batch_class_counts.append([sum(i in positives for i in indices), sum(i in backgrounds for i in indices)])
            if not any(i in positives for i in indices) or not any(i in backgrounds for i in indices):
                raise StrokeSeededTrainingError("stratified batch has one class only")
            x = features[list(indices)].to(device)
            obj = objectness[list(indices)].to(device)
            target = box_targets[list(indices)].to(device)
            mask = positive_mask[list(indices)].to(device)
            if any(t.device != device for t in (x, obj, target, mask)):
                raise StrokeSeededTrainingError("training tensor is not on the RX 6600 XT")
            optimizer.zero_grad(set_to_none=True)
            logits, prediction = model(x)
            per_cell = nn.functional.binary_cross_entropy_with_logits(logits, obj, reduction="none")
            pos_loss = per_cell[obj.bool()].mean()
            neg_loss = per_cell[(~obj.bool())].mean()
            objectness_loss = 0.5 * (pos_loss + neg_loss)
            expanded = mask.expand(-1, 4, -1, -1)
            box_loss = nn.functional.smooth_l1_loss(prediction[expanded], target[expanded])
            loss = objectness_loss + box_loss
            if not torch.isfinite(loss):
                raise StrokeSeededTrainingError("non-finite GPU training loss")
            loss.backward()
            optimizer.step()
            _RUN["steps"] += 1
            value, pvalue, nvalue = float(loss.detach().cpu()), float(pos_loss.detach().cpu()), float(neg_loss.detach().cpu())
            losses.append(value); epoch_losses.append(value); positive_losses.append(pvalue); background_losses.append(nvalue)
        cells = helpers.evaluate_train_fit(model, features, objectness, box_targets, positive_mask, device, torch)
        images = _image_metrics(model, features, boxes, device, torch, config)
        epoch_reports.append({"epoch": epoch + 1, "loss_mean": sum(epoch_losses) / len(epoch_losses),
                              "cell_metrics": cells})
    expected_steps = int(config["expected_optimizer_steps"])
    if _RUN["steps"] != expected_steps or len(losses) != expected_steps:
        raise StrokeSeededTrainingError("optimizer step count differs from frozen config")
    after_cells = helpers.evaluate_train_fit(model, features, objectness, box_targets, positive_mask, device, torch)
    after_images = _image_metrics(model, features, boxes, device, torch, config)

    checkpoint = output / "checkpoint.pt"
    torch.save({"state_dict": model.state_dict(), "model_identity": config["model_identity"],
                "architecture": config["architecture"], "box_parameterization": config["box_parameterization"],
                "receptive_field_pixels": receptive_field, "input_manifest_sha256": admitted["manifest_sha256"],
                "runtime": runtime, "optimizer_steps": expected_steps, "recipe_id": config["recipe_id"],
                "model_seed": config["model_seed"], "initialization": config["initialization"]}, checkpoint)
    report = {
        "schema": "graphreader.stroke-seeded-xtick-v1-gpu-train-fit-report.v1",
        "status": "completed-unregistered-exploratory-train-fit",
        "recipe_id": config["recipe_id"], "model_identity": config["model_identity"],
        "config_sha256": digest(config_bytes), "trainer_sha256": digest(Path(__file__).read_bytes()),
        "input_manifest_path": config["train_manifest_path"], "input_manifest_sha256": admitted["manifest_sha256"],
        "input_row_count": 681, "admitted_positive_count": len(positives),
        "admitted_background_count": len(backgrounds), "excluded_count": admitted["kind_counts"]["excluded"],
        "source_group_count": len(admitted["group_counts"]),
        "source_group_trainable_crop_counts": admitted["group_counts"],
        "source_group_min_crop_count": min(admitted["group_counts"].values()),
        "source_group_max_crop_count": max(admitted["group_counts"].values()),
        "epochs": int(config["epochs"]), "batch_size": int(config["batch_size"]),
        "steps_per_epoch": int(config["steps_per_epoch"]), "optimizer_steps": expected_steps,
        "model_seed": int(config["model_seed"]), "shuffle_seed": int(config["shuffle_seed"]),
        "initialization": config["initialization"], "checkpoint_loaded": False,
        "runtime": runtime, "output_grid": [12, 32], "receptive_field_pixels": receptive_field,
        "class_balanced_objectness_loss": config["balanced_objectness_loss"], "box_loss": config["box_loss"],
        "loss_first": losses[0], "loss_last": losses[-1],
        "positive_cell_bce_first": positive_losses[0], "positive_cell_bce_last": positive_losses[-1],
        "background_cell_bce_first": background_losses[0], "background_cell_bce_last": background_losses[-1],
        "train_fit_before_cell_metrics": before_cells, "train_fit_after_cell_metrics": after_cells,
        "train_fit_before_image_metrics": before_images, "epoch_train_fit": epoch_reports,
        "train_fit_after_image_metrics": after_images,
        "image_metric_contract": {"score_thresholds": config["score_thresholds"],
            "nms_iou_threshold": config["nms_iou_threshold"], "match_iou_threshold": config["match_iou_threshold"],
            "maximum_detections_per_image": config["maximum_detections_per_image"],
            "top_box": "highest-confidence valid decoded grid box before score thresholding"},
        "train_fit_is_held_out_validation": False,
        "dev_sealed_private_probe_inputs_read": False,
        "production_approval": False, "release_eligible": False,
        "checkpoint_path": checkpoint.relative_to(REPO_ROOT).as_posix(),
        "checkpoint_sha256": digest(checkpoint.read_bytes()),
        "finished_utc": datetime.now(timezone.utc).isoformat(),
    }
    (output / "report.json").write_bytes(canonical_json(report))
    return report


def run_training() -> dict[str, Any]:
    _RUN.update(phase="starting", steps=0)
    config, config_bytes = load_config()
    output = REPO_ROOT / str(config["planned_output_root"])
    if output.parent != (REPO_ROOT / "artifacts") or output.exists() or output.is_symlink():
        raise StrokeSeededTrainingError("planned output is unsafe or already exists")
    helpers._checked_chain(REPO_ROOT, output.parent, "stroke-seeded run output parent")
    output.mkdir(parents=True, exist_ok=False)
    start = {"schema": "graphreader.stroke-seeded-xtick-v1-gpu-run.v1", "status": "started",
             "recipe_id": config["recipe_id"], "config_sha256": digest(config_bytes),
             "trainer_sha256": digest(Path(__file__).read_bytes()), "started_utc": datetime.now(timezone.utc).isoformat(),
             "optimizer_steps_completed": 0}
    (output / "started.json").write_bytes(canonical_json(start))
    try:
        report = _run_training(config, config_bytes, output)
        return report
    except Exception as error:
        receipt = {"schema": "graphreader.stroke-seeded-xtick-v1-gpu-failure.v1", "status": "failed",
                   "recipe_id": config.get("recipe_id"), "config_sha256": digest(config_bytes),
                   "trainer_sha256": digest(Path(__file__).read_bytes()), "failure_type": type(error).__name__,
                   "failure_message": str(error), "phase": _RUN["phase"],
                   "optimizer_steps_completed": _RUN["steps"], "failed_utc": datetime.now(timezone.utc).isoformat(),
                   "dev_sealed_private_inputs_read": False}
        (output / "failure.json").write_bytes(canonical_json(receipt))
        raise


if __name__ == "__main__":
    print(json.dumps(run_training(), sort_keys=True, indent=2))
