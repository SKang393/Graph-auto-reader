# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import pytest

from ml.ocr.legend_coverage_db_head_v45 import verify_export as verifier


def _result():
    return {
        "optimizer_steps": 8160,
        "epochs": 240,
        "panel_count": 34,
        "positive_panel_count": 34,
        "empty_positive_panels": 0,
        "intraop_threads": 12,
        "seed": verifier.runner.RECIPE["seed"],
        "batch_size": 1,
        "learning_rate": 0.0001,
        "weight_decay": 0.0001,
        "dice_epsilon": 1e-6,
        "torch_backend": verifier.runner.engine.TORCH_BACKEND,
        "trainable_parameter_names": list(verifier.runner.engine.TRAINABLE_PARAMETER_NAMES),
        "selected_epoch": 1,
        "selected_trainable_state_sha256": "c" * 64,
        "frozen_batch_norm_sha256_before": "a" * 64,
        "frozen_batch_norm_sha256_after": "a" * 64,
        "epoch_losses": [
            {
                "epoch": epoch,
                "optimizer_steps": epoch * 34,
                "full_train_loss": 0.5,
                "training_step_mean_loss": 0.5,
                "full_positive_dice_loss": 0.5,
                "full_empty_negative_loss": None,
                "draw_order_sha256": "b" * 64,
            }
            for epoch in range(1, 241)
        ],
    }


def test_export_requires_earliest_minimum_v45_training_checkpoint():
    verifier.validate_training_result(_result())


@pytest.mark.parametrize(
    "defect",
    [
        "steps",
        "panels",
        "threads",
        "omission",
        "epoch",
        "nonfinite",
        "partition",
        "selection",
        "normalization",
        "state",
    ],
)
def test_export_rejects_inconsistent_training_evidence(defect):
    result = _result()
    if defect == "steps":
        result["optimizer_steps"] = 6720
    elif defect == "panels":
        result["panel_count"] = 28
    elif defect == "threads":
        result["intraop_threads"] = 16
    elif defect == "omission":
        result["epoch_losses"].pop()
    elif defect == "epoch":
        result["epoch_losses"][0]["epoch"] = True
    elif defect == "nonfinite":
        result["epoch_losses"][0]["full_train_loss"] = float("nan")
    elif defect == "partition":
        result["epoch_losses"][0]["full_positive_dice_loss"] = 0.4
    elif defect == "selection":
        result["selected_epoch"] = 240
    elif defect == "normalization":
        result["frozen_batch_norm_sha256_after"] = "d" * 64
    else:
        result["selected_trainable_state_sha256"] = "short"
    with pytest.raises(ValueError):
        verifier.validate_training_result(result)
