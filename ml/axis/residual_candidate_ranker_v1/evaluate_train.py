# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Evaluate the frozen residual model on its synthetic training candidates.

This is CPU inference, not model training. No development or sealed data is read.
"""
from hashlib import sha256
import json
import numpy as np

from ml.axis.residual_candidate_ranker_v1 import runner


def _digest(raw: bytes) -> str:
    return sha256(raw).hexdigest()


def score(features: list[list[float]], checkpoint: dict) -> np.ndarray:
    """Reproduce the trained network with single-precision CPU inference."""
    state = checkpoint["state_dict"]
    inputs = np.asarray(features, dtype=np.float32)
    scale = np.asarray(state["feature_scale"], dtype=np.float32)
    hidden_weight = np.asarray(state["hidden.weight"], dtype=np.float32)
    hidden_bias = np.asarray(state["hidden.bias"], dtype=np.float32)
    output_weight = np.asarray(state["output.weight"], dtype=np.float32)
    output_bias = np.asarray(state["output.bias"], dtype=np.float32)
    if (inputs.ndim != 2 or inputs.shape[1] != 17 or not len(inputs) or
            scale.shape != (17,) or hidden_weight.shape != (32, 17) or
            hidden_bias.shape != (32,) or output_weight.shape != (1, 32) or
            output_bias.shape != (1,) or checkpoint.get("residual_scale") != runner.RESIDUAL_SCALE):
        raise ValueError("Residual model or input dimensions differ from the fixed recipe")
    arrays = (inputs, scale, hidden_weight, hidden_bias, output_weight, output_bias)
    if not all(np.isfinite(value).all() for value in arrays) or not np.array_equal(
            scale, np.asarray(runner.SCALE, dtype=np.float32)):
        raise ValueError("Residual model or inputs are nonfinite or have an unexpected scale")
    hidden = np.tanh((inputs * scale) @ hidden_weight.T + hidden_bias)
    result = inputs[:, 0] + np.float32(runner.RESIDUAL_SCALE) * (hidden @ output_weight.T + output_bias)[:, 0]
    if not np.isfinite(result).all():
        raise ValueError("Residual scores are not finite")
    return result


def evaluate(groups: list[dict], checkpoint: dict) -> dict:
    changed = improved = equal = worsened = 0
    native_errors, selected_errors = [], []
    for group in groups:
        rows = group["rows"]
        if not rows or [row["rank"] for row in rows] != list(range(1, len(rows) + 1)):
            raise ValueError("Training candidate ranks are missing or out of order")
        predicted = int(np.argmax(score([row["features"] for row in rows], checkpoint)))
        native, selected = float(rows[0]["target_error"]), float(rows[predicted]["target_error"])
        if not np.isfinite(native) or not np.isfinite(selected) or min(native, selected) < 0:
            raise ValueError("Training target error is invalid")
        changed += predicted != 0
        improved += selected < native - 1e-12
        worsened += selected > native + 1e-12
        equal += abs(selected - native) <= 1e-12
        native_errors.append(native)
        selected_errors.append(selected)
    return {
        "scored_panels": len(groups), "changed_selection_panels": changed,
        "improved": improved, "equal": equal, "worsened": worsened,
        "native_mean_normalized_corner_error": sum(native_errors) / len(native_errors),
        "candidate_mean_normalized_corner_error": sum(selected_errors) / len(selected_errors),
    }


def main() -> None:
    root, out = runner.ROOT, runner.OUT
    config_raw = (root / runner.CONFIG).read_bytes()
    checkpoint_raw = (out / "checkpoint.json").read_bytes()
    report_raw = (out / "report.json").read_bytes()
    group_raw = (root / runner.GROUPS).read_bytes()
    report = json.loads(report_raw)
    checkpoint = json.loads(checkpoint_raw)
    seal = json.loads((root / "ml/markers/training-seals" / runner.TASK / runner.REVISION /
                       runner.CANDIDATE / "result.json").read_bytes())
    if (report.get("status") != "training_complete_unapproved" or report.get("steps") != runner.STEPS or
            report.get("device") != "AMD Radeon RX 6600 XT" or report.get("cpu_fallback") is not False or
            report.get("development_reads") != 0 or report.get("sealed_reads") != 0 or
            report.get("config_sha256") != _digest(config_raw) or report.get("groups_sha256") != runner.GROUP_SHA or
            report.get("checkpoint_sha256") != _digest(checkpoint_raw) or _digest(group_raw) != runner.GROUP_SHA or
            checkpoint.get("config_sha256") != _digest(config_raw) or checkpoint.get("production_approved") is not False or
            seal.get("status") != report["status"] or seal.get("report_sha256") != _digest(report_raw)):
        raise ValueError("Frozen model, training provenance, or synthetic data binding differs")
    groups = json.loads(group_raw)["groups"]
    if len(groups) != 93 or sum(len(group["rows"]) for group in groups) != 27776:
        raise ValueError("Synthetic training denominator differs")
    result = {
        "schema": "graphreader.axis-residual-ranker-train-evaluation.v1",
        "split": "synthetic_train", "unscored_fallback_panels": 3,
        "checkpoint_sha256": _digest(checkpoint_raw), "training_report_sha256": _digest(report_raw),
        "groups_sha256": _digest(group_raw), "production_approved": False,
        "development_reads": 0, "sealed_reads": 0,
        **evaluate(groups, checkpoint),
    }
    raw = (json.dumps(result, sort_keys=True, separators=(",", ":"), allow_nan=False) + "\n").encode()
    runner.write_new(out / "train-score.json", raw)
    print(raw.decode(), end="")


if __name__ == "__main__":
    main()
