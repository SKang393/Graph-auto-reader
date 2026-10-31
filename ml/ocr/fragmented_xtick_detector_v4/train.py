# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""RX 6600 XT-only V4 training; imports P4 architecture/loss helpers, not its input loader."""

from __future__ import annotations

from datetime import datetime, timezone
from hashlib import sha256
import json
from pathlib import Path
import sys
from typing import Any

ROOT = Path(__file__).resolve().parent
REPO_ROOT = ROOT.parents[2]
sys.path.insert(0, str(REPO_ROOT))
from ml.ocr.fragmented_xtick_detector_v3.gpu_train import (
    GPU_PYTHON, _annotation_target, build_spatial_locator, evaluate_train_fit, feature_receptive_field,
    require_rx_6600_xt, stratified_batches, target_assignments,
)
from ml.ocr.fragmented_xtick_detector_v4.materialize import canonical_json
from ml.ocr.fragmented_xtick_detector_v4.admission import load_train_inputs as admitted_load_train_inputs

CONFIG = ROOT / "training" / "p1.json"
_RUN_PROGRESS: dict[str, Any] = {"phase": "starting", "steps": 0}


class V4TrainingError(RuntimeError):
    pass


def digest(data: bytes) -> str:
    return sha256(data).hexdigest()


def load_train_inputs(torch: Any, manifest_path: Path) -> tuple[dict[str, Any], list[Any], list[tuple[float, float, float, float] | None]]:
    relative = manifest_path.relative_to(REPO_ROOT).as_posix()
    manifest, values, boxes, _ = admitted_load_train_inputs(torch, relative)
    return manifest, values, boxes


def hard_negative_mask(logits: Any, objectness_target: Any, top_k: int) -> Any:
    """Select each image's highest-scoring non-objectness cells, excluding positives."""
    if top_k < 1 or logits.shape != objectness_target.shape or logits.ndim != 4:
        raise ValueError("hard-negative mining expects matching NCHW logits and positive mask")
    torch = __import__("torch")
    negatives = ~objectness_target.bool()
    selected = torch.zeros_like(negatives)
    for batch_index in range(logits.shape[0]):
        eligible = torch.nonzero(negatives[batch_index].reshape(-1), as_tuple=False).flatten()
        count = min(int(top_k), int(eligible.numel()))
        if count:
            scores = logits[batch_index].reshape(-1)[eligible]
            chosen = eligible[torch.topk(scores, k=count, largest=True, sorted=False).indices]
            selected[batch_index].view(-1)[chosen] = True
    return selected


def _run_training(config_path: Path, output: Path, config_bytes: bytes, config: dict[str, Any]) -> dict[str, Any]:
    if Path(sys.executable).resolve() != GPU_PYTHON.resolve():
        raise V4TrainingError("V4 optimizer must run via the project GPU launcher")
    import torch
    from torch import nn

    if config.get("optimizer_execution_authorized") is not True or config.get("probe_inputs_read_during_training") is not False:
        raise V4TrainingError("V4 train-only execution is not authorized")
    if (config.get("allowed_input_roots") != ["artifacts/fragmented-xtick-v4-diverse-train-v1"]
            or config.get("dataset_family") != "fragmented-xtick-v4-diverse-train"
            or config.get("train_manifest_path") != "artifacts/fragmented-xtick-v4-diverse-train-v1/manifest.json"):
        raise V4TrainingError("V4 config input root/family is outside its train-only boundary")
    if config.get("hard_negative_top_k_per_image", 0) < 0:
        raise V4TrainingError("V4 hard-negative count cannot be negative")
    _RUN_PROGRESS.update(phase="rx6600xt-binding")
    runtime = require_rx_6600_xt(torch)
    device = torch.device(runtime["device"])
    torch.manual_seed(int(config["model_seed"]))
    _RUN_PROGRESS.update(phase="train-input-admission")
    manifest, values, boxes, manifest_sha = admitted_load_train_inputs(
        torch, config["train_manifest_path"], config.get("train_manifest_sha256"))
    features = torch.stack(values).unsqueeze(1)
    assignments = target_assignments(tuple(boxes))
    objectness = torch.zeros((len(boxes), 1, 12, 32), dtype=torch.float32)
    targets = torch.zeros((len(boxes), 4, 12, 32), dtype=torch.float32)
    mask = torch.zeros((len(boxes), 1, 12, 32), dtype=torch.bool)
    for index, item in enumerate(assignments):
        if item is not None:
            row, column, target = item
            objectness[index, 0, row, column] = 1
            targets[index, :, row, column] = torch.tensor(target)
            mask[index, 0, row, column] = True
    model = build_spatial_locator(torch).to(device)
    if feature_receptive_field(model.features, torch) != 31 or any(p.device != device for p in model.parameters()):
        raise V4TrainingError("V4 architecture/GPU binding check failed")
    output_shape = list(model(features[:1].to(device))[0].shape[-2:])
    if output_shape != [12, 32]:
        raise V4TrainingError("V4 model spatial output shape changed")
    optimizer = torch.optim.AdamW(model.parameters(), lr=float(config["learning_rate"]))
    _RUN_PROGRESS.update(phase="optimizer-training")
    before = evaluate_train_fit(model, features, objectness, targets, mask, device, torch)
    positive = tuple(i for i, box in enumerate(boxes) if box is not None)
    negative = tuple(i for i, box in enumerate(boxes) if box is None)
    losses, positive_bce, background_bce, epoch_metrics, batch_classes = [], [], [], [], []
    for epoch in range(int(config["epochs"])):
        batches = stratified_batches(positive, negative, int(config["batch_size"]), int(config["shuffle_seed"]) + epoch)
        seen = [index for batch in batches for index in batch]
        if len(seen) != len(boxes) or len(set(seen)) != len(boxes):
            raise V4TrainingError("stratified epoch omitted or duplicated samples")
        for indices in batches:
            batch = torch.utils.data.default_collate([(features[i], objectness[i], targets[i], mask[i]) for i in indices])
            x, obj, target, positive_mask = (value.to(device) for value in batch)
            p_count = sum(i in positive for i in indices)
            batch_classes.append([p_count, len(indices) - p_count])
            if p_count == 0 or p_count == len(indices) or any(value.device != device for value in (x, obj, target, positive_mask)):
                raise V4TrainingError("mixed batch or RX 6600 XT tensor residency contract failed")
            optimizer.zero_grad(set_to_none=True)
            logits, prediction = model(x)
            if logits.shape != obj.shape or prediction.shape != target.shape:
                raise V4TrainingError("V4 model output/target shape mismatch")
            per_cell = nn.functional.binary_cross_entropy_with_logits(logits, obj, reduction="none")
            pos_mean = per_cell[obj.bool()].mean()
            top_k = int(config.get("hard_negative_top_k_per_image", 0))
            if top_k:
                mined_mask = hard_negative_mask(logits.detach(), obj, top_k)
                bg_mean = per_cell[mined_mask].mean()
            else:
                mined_mask = ~obj.bool()
                bg_mean = per_cell[mined_mask].mean()
            obj_loss = 0.5 * (pos_mean + bg_mean)
            expanded = positive_mask.expand(-1, 4, -1, -1)
            box_loss = nn.functional.smooth_l1_loss(prediction[expanded], target[expanded])
            loss = obj_loss + box_loss
            if not torch.isfinite(loss):
                raise V4TrainingError("V4 loss became non-finite")
            loss.backward(); optimizer.step()
            _RUN_PROGRESS["steps"] += 1
            losses.append(float(loss.detach().cpu())); positive_bce.append(float(pos_mean.detach().cpu())); background_bce.append(float(bg_mean.detach().cpu()))
        epoch_metrics.append(evaluate_train_fit(model, features, objectness, targets, mask, device, torch))
    expected_steps = int(config["epochs"]) * (len(boxes) // int(config["batch_size"]))
    if len(losses) != expected_steps:
        raise V4TrainingError("V4 optimizer steps differ from configured duration")
    after = evaluate_train_fit(model, features, objectness, targets, mask, device, torch)
    checkpoint = output / "checkpoint.pt"
    torch.save({"state_dict": model.state_dict(), "model_id": config["model_identity"], "architecture": config["architecture"],
        "box_parameterization": config["box_parameterization"], "receptive_field_pixels": 31,
        "input_manifest_sha256": manifest_sha, "runtime": runtime,
        "optimizer_steps": expected_steps, "recipe_id": config["recipe_id"]}, checkpoint)
    entrypoint = Path(sys.argv[0]).resolve()
    report = {"schema": "graphreader.ocr-fragmented-xtick-v4-training-report.v1", "status": "completed-unregistered-exploratory",
        "recipe_id": config["recipe_id"], "model_identity": config["model_identity"], "config_sha256": digest(config_bytes),
        "training_entrypoint_path": entrypoint.relative_to(REPO_ROOT).as_posix(), "training_entrypoint_sha256": digest(entrypoint.read_bytes()),
        "trainer_sha256": digest(Path(__file__).read_bytes()), "materializer_sha256": digest((ROOT / "materialize.py").read_bytes()),
        "train_manifest_sha256": manifest_sha,
        "input_case_count": len(boxes), "positive_count": len(positive), "background_only_count": len(negative),
        "epochs": int(config["epochs"]), "batch_size": int(config["batch_size"]), "optimizer_steps": expected_steps,
        "batch_class_counts_first_epoch": batch_classes[:len(batches)], "runtime": runtime, "feature_map_shape": output_shape,
        "spatial_receptive_field_pixels": 31, "loss_first": losses[0], "loss_last": losses[-1],
        "positive_cell_bce_first": positive_bce[0], "positive_cell_bce_last": positive_bce[-1],
        "background_cell_bce_first": background_bce[0], "background_cell_bce_last": background_bce[-1],
        "hard_negative_top_k_per_image": int(config.get("hard_negative_top_k_per_image", 0)),
        "objectness_loss": "mean positive-cell BCE plus mean selected-negative-cell BCE, equally weighted",
        "train_fit_before": before, "epoch_train_fit": epoch_metrics, "train_fit_after": after,
        "train_fit_is_held_out_validation": False, "probe_inputs_read_during_training": False,
        "dev_sealed_private_inputs_read": False, "checkpoint_sha256": digest(checkpoint.read_bytes()),
        "checkpoint_path": checkpoint.relative_to(REPO_ROOT).as_posix(), "started_finished_utc": datetime.now(timezone.utc).isoformat(),
        "production_approval": False, "release_eligible": False}
    (output / "report.json").write_bytes(canonical_json(report))
    return report


def run_training(config_path: Path = CONFIG) -> dict[str, Any]:
    _RUN_PROGRESS.update(phase="starting", steps=0)
    config_bytes = config_path.read_bytes()
    config = json.loads(config_bytes)
    output = REPO_ROOT / config["planned_output_root"]
    if output.exists() or output.is_symlink() or output.parent != (REPO_ROOT / "artifacts"):
        raise V4TrainingError("V4 run output must be a fresh project artifacts directory")
    output.mkdir(parents=True, exist_ok=False)
    (output / "started.json").write_bytes(canonical_json({"status": "started", "recipe_id": config.get("recipe_id"),
        "config_sha256": digest(config_bytes), "started_utc": datetime.now(timezone.utc).isoformat()}))
    try:
        return _run_training(config_path, output, config_bytes, config)
    except Exception as error:
        receipt = {"schema": "graphreader.ocr-fragmented-xtick-v4-failure-receipt.v1", "status": "failed",
            "recipe_id": config.get("recipe_id"), "config_sha256": digest(config_bytes),
            "trainer_sha256": digest(Path(__file__).read_bytes()), "failure_type": type(error).__name__,
            "failure_message": str(error), "failure_phase": _RUN_PROGRESS["phase"],
            "timestamp_utc": datetime.now(timezone.utc).isoformat(), "optimizer_steps_completed": _RUN_PROGRESS["steps"]}
        (output / "failure.json").write_bytes(canonical_json(receipt))
        raise


if __name__ == "__main__":
    print(json.dumps(run_training(), indent=2, sort_keys=True))
