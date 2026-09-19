# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Verify the V45 checkpoint, patch containment, and all 43 captured tensors."""
from __future__ import annotations

import argparse
from dataclasses import asdict
import json
import math
from pathlib import Path
import time

import numpy as np
import onnxruntime as ort
import torch

from ml.markers.gate_seal import canonical_json_bytes, sha256_file, verify_bound_source_snapshot
from ml.ocr.legend_coverage_db_head_v45 import train_p1 as runner
from ml.ocr.official_bakeoff import (
    captured_head_features as capture_api,
    supplemental_head_features as supplemental_api,
)


STAGE_SCHEMA = "graphreader.ocr-legend-coverage-v45-training-stage.v1"
PARITY_SCHEMA = "graphreader.ocr-legend-coverage-v45-trained-export-parity.v1"
MODEL_NAME = "detector-legend-coverage-v45-p1.onnx"
TOLERANCE = 1e-5
SUPPLEMENTAL_FEATURE_REPORT_PATH = "artifacts/goal22-runs/ocr-supplemental-head-features-v1/report.json"
SUPPLEMENTAL_FEATURE_REPORT_SHA256 = "4b2710b380be1b16d5fef7d9184920051c413304353ed821480bda0ab3a16d05"
FROZEN_HEAD_SOURCE_SHA256 = "6171df9298f785360695353087344229a8f761cfda51bb3aa728ddab10997703"


def _sha256(value, label):
    if (
        not isinstance(value, str)
        or len(value) != 64
        or any(character not in "0123456789abcdef" for character in value)
    ):
        raise ValueError(f"{label} must be a lowercase SHA-256 value")
    return value


def validate_training_result(result):
    exact = {
        "optimizer_steps": 8160,
        "epochs": 240,
        "panel_count": 34,
        "positive_panel_count": 34,
        "empty_positive_panels": 0,
        "intraop_threads": 12,
        "seed": runner.RECIPE["seed"],
        "batch_size": 1,
        "learning_rate": 0.0001,
        "weight_decay": 0.0001,
        "dice_epsilon": 1e-6,
        "torch_backend": runner.engine.TORCH_BACKEND,
        "trainable_parameter_names": list(runner.engine.TRAINABLE_PARAMETER_NAMES),
    }
    if any(type(result.get(key)) is not type(value) or result.get(key) != value for key, value in exact.items()):
        raise ValueError("Training result differs from the authorized V45 recipe")
    rows = result.get("epoch_losses")
    if not isinstance(rows, list) or len(rows) != 240:
        raise ValueError("Training omitted epoch evidence")
    for epoch, row in enumerate(rows, 1):
        if (
            not isinstance(row, dict)
            or type(row.get("epoch")) is not int
            or row["epoch"] != epoch
            or type(row.get("optimizer_steps")) is not int
            or row["optimizer_steps"] != epoch * 34
            or row.get("full_empty_negative_loss") is not None
        ):
            raise ValueError("Epoch inventory changed")
        for key in ("full_train_loss", "training_step_mean_loss", "full_positive_dice_loss"):
            value = row.get(key)
            if type(value) not in (float, int) or not math.isfinite(value) or value < 0:
                raise ValueError("Epoch loss is not finite and nonnegative")
        if row["full_train_loss"] != row["full_positive_dice_loss"]:
            raise ValueError("Positive-only training loss partition changed")
        _sha256(row.get("draw_order_sha256"), "epoch draw order")
    selected = result.get("selected_epoch")
    earliest = min(range(1, 241), key=lambda index: rows[index - 1]["full_train_loss"])
    if type(selected) is not int or selected != earliest:
        raise ValueError("Checkpoint was not the earliest minimum train loss")
    if result.get("frozen_batch_norm_sha256_before") != result.get("frozen_batch_norm_sha256_after"):
        raise ValueError("Frozen normalization changed")
    for key in ("selected_trainable_state_sha256", "frozen_batch_norm_sha256_after"):
        _sha256(result.get(key), key)


def _captured_inputs_and_features(root, files):
    capture_path = runner._bound(root, files["capture_report"])
    historical_capture, historical_tensors = capture_api.load_capture_tensors(
        capture_path, files["capture_report"]["sha256"]
    )
    parity_path = runner._bound(root, files["parity_report"])
    historical_features, _, _ = runner.v39._validate_parity_and_features(
        runner._json(parity_path),
        parity_path.parent,
        historical_capture,
        files["capture_report"]["sha256"],
        sha256_file(root / "ml/ocr/official_bakeoff/captured_head_features.py"),
        sha256_file(root / "ml/ocr/official_bakeoff/frozen_trunk_head.py"),
        files["feature_model"]["sha256"],
    )
    fixed = {
        "supplemental_capture_report": (
            supplemental_api.CAPTURE_PATH.as_posix(), supplemental_api.CAPTURE_SHA256
        ),
        "supplemental_feature_model": (None, supplemental_api.FEATURE_MODEL_SHA256),
        "supplemental_composition_report": (
            supplemental_api.COMPOSITION_PATH.as_posix(), supplemental_api.COMPOSITION_SHA256
        ),
    }
    for key, (path, digest) in fixed.items():
        descriptor = files[key]
        if descriptor.get("sha256") != digest or (path is not None and descriptor.get("path") != path):
            raise ValueError(f"V45 {key} binding changed")
        runner._bound(root, descriptor)
    if files["supplemental_feature_report"] != {
        "path": SUPPLEMENTAL_FEATURE_REPORT_PATH,
        "sha256": SUPPLEMENTAL_FEATURE_REPORT_SHA256,
    }:
        raise ValueError("V45 supplemental feature report binding changed")
    _, supplemental_capture, supplemental_tensors = supplemental_api.authenticate_inputs(
        repository_root=root
    )
    supplemental_report_path = runner._bound(root, files["supplemental_feature_report"])
    _, supplemental_rows = supplemental_api.load_cached_features(
        repository_root=root,
        feature_report=Path(files["supplemental_feature_report"]["path"]),
        expected_report_sha256=files["supplemental_feature_report"]["sha256"],
    )
    supplemental_features = {
        row["panel_id"]: (values, row["input_sha256"])
        for row, values in supplemental_rows
    }
    if supplemental_report_path.parent / "frozen-trunk.onnx" != runner._bound(
        root, files["supplemental_feature_model"]
    ):
        raise ValueError("Supplemental feature model path changed")
    collision = set(historical_features) & set(supplemental_features)
    features = {**historical_features, **supplemental_features}
    tensors = tuple(historical_tensors) + tuple(supplemental_tensors)
    panel_ids = [panel["panel_id"] for panel, _ in tensors]
    splits = [panel["split"] for panel, _ in tensors]
    if (
        collision
        or len(features) != 43
        or len(tensors) != 43
        or len(set(panel_ids)) != 43
        or set(features) != set(panel_ids)
        or splits.count("train") != 34
        or splits.count("validation") != 9
        or supplemental_capture.get("panel_count") != 6
    ):
        raise ValueError("V45 combined capture inventory changed")
    return tensors, features


def verify(stage_path, stage_sha, output_path, source_sha, root=runner.REPOSITORY_ROOT):
    started = time.perf_counter()
    root = Path(root).resolve()
    stage_path = (root / stage_path).resolve()
    output = (root / output_path).resolve()
    if (
        not stage_path.is_relative_to(root / "artifacts")
        or not output.is_relative_to(root / "artifacts")
        or output.exists()
        or sha256_file(stage_path) != stage_sha
        or sha256_file(Path(__file__)) != source_sha
    ):
        raise ValueError("Export verification inputs or destination changed")
    runner._cpu_budget()
    stage = runner._json(stage_path)
    fixed = {
        "schema": STAGE_SCHEMA,
        "status": "trained_pending_export_parity_and_dev",
        "task": runner.TASK,
        "revision": runner.REVISION,
        "candidate_id": "P1",
        "private_reads": 0,
        "sealed_reads": 0,
        "production_approved": False,
    }
    if any(type(stage.get(key)) is not type(value) or stage.get(key) != value for key, value in fixed.items()):
        raise ValueError("Training stage is not the authorized completed experiment")
    prepared = runner.prepare_training(root)
    authorization = stage["training_authorization"]
    expected_authorization = {
        "task": runner.TASK,
        "revision": runner.REVISION,
        "candidate_id": "P1",
        "candidate_config_path": runner.CONFIG_PATH.as_posix(),
        "candidate_config_sha256": prepared.config_sha256,
        "runner_source_bundle_sha256": prepared.config["expected_runner_source_bundle_sha256"],
        "runner_source_paths": sorted(path.as_posix() for path in runner.RUNNER_SOURCE_PATHS),
    }
    if not isinstance(authorization, dict) or any(
        authorization.get(key) != value for key, value in expected_authorization.items()
    ):
        raise ValueError("Training authorization changed")
    snapshot = (root / authorization["source_snapshot_path"]).resolve()
    if not snapshot.is_relative_to(root):
        raise ValueError("Source snapshot escaped repository")
    verify_bound_source_snapshot(root, snapshot, authorization["source_snapshot_sha256"])
    if (
        stage.get("feature_inventory_sha256") != prepared.feature_inventory_sha256
        or stage.get("target_inventory_sha256") != prepared.target_inventory_sha256
    ):
        raise ValueError("Training input inventory changed")
    validate_training_result(stage["training"])
    files = prepared.config["bound_files"]
    parent = runner._bound(root, files["parent_model"])
    if (
        files["parent_model"]["sha256"] != runner.frozen_trunk_head.REVIEWED_DETECTOR_SHA256
        or files["feature_model"]["sha256"] != supplemental_api.FEATURE_MODEL_SHA256
        or sha256_file(root / "ml/ocr/official_bakeoff/frozen_trunk_head.py")
        != FROZEN_HEAD_SOURCE_SHA256
    ):
        raise ValueError("Reviewed parent or frozen trunk changed")
    checkpoint = stage_path.parent / "selected-head.pt"
    model = stage_path.parent / MODEL_NAME
    if sha256_file(checkpoint) != stage.get("checkpoint_sha256") or sha256_file(model) != stage.get("model_sha256"):
        raise ValueError("Trained artifacts changed")
    bundle = runner.frozen_trunk_head.extract_frozen_trunk_head(parent)
    bundle.head.load_state_dict(torch.load(checkpoint, map_location="cpu", weights_only=True), strict=True)
    bundle.head.eval()
    training = stage["training"]
    if (
        runner.frozen_head_training._trainable_state_sha256(bundle.head)
        != training["selected_trainable_state_sha256"]
        or runner.frozen_head_training._frozen_bn_sha256(bundle.head)
        != training["frozen_batch_norm_sha256_after"]
    ):
        raise ValueError("Checkpoint does not match selected training state")
    output.mkdir(parents=True)
    patch = runner.frozen_trunk_head.patch_head_constants(parent, output / "reproduced.onnx", bundle.head)
    runner.v40._validate_patch(patch, model)
    expected_constants = set(runner.frozen_trunk_head.TRAINABLE_CONSTANT_SHAPES)
    if set(patch.changed_constants) != expected_constants or len(patch.changed_constants) != 9:
        raise ValueError("Trained export changed outside the nine allowlisted head constants")
    if patch.output_sha256 != stage["model_sha256"]:
        raise ValueError("Checkpoint patch does not reproduce trained ONNX bytes")
    tensors, features = _captured_inputs_and_features(root, files)
    if stage["feature_inventory_sha256"] != prepared.feature_inventory_sha256:
        raise ValueError("Combined feature inventory changed")
    options = ort.SessionOptions()
    options.intra_op_num_threads = options.inter_op_num_threads = 1
    options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    session = ort.InferenceSession(str(model), options, providers=["CPUExecutionProvider"])
    previous = (
        torch.get_num_threads(),
        torch.are_deterministic_algorithms_enabled(),
        torch.is_deterministic_algorithms_warn_only_enabled(),
    )
    rows = []
    try:
        torch.set_num_threads(12)
        torch.use_deterministic_algorithms(True)
        with torch.inference_mode(), torch.backends.mkldnn.flags(enabled=False):
            for panel, values in tensors:
                runner._cpu_budget()
                cached, input_sha = features[panel["panel_id"]]
                if input_sha != panel["tensor"]["sha256"]:
                    raise ValueError("Cached feature and detector tensor differ")
                expected_output = bundle.head(torch.from_numpy(cached.copy())).numpy()
                actual = session.run(
                    [runner.frozen_trunk_head.MODEL_OUTPUT_NAME],
                    {runner.frozen_trunk_head.MODEL_INPUT_NAME: values},
                )[0]
                if (
                    actual.shape != expected_output.shape
                    or not np.isfinite(actual).all()
                    or not np.isfinite(expected_output).all()
                ):
                    raise ValueError("Trained parity output is invalid")
                rows.append(
                    {
                        "panel_id": panel["panel_id"],
                        "split": panel["split"],
                        "input_sha256": input_sha,
                        "maximum_absolute_error": float(np.max(np.abs(actual - expected_output))),
                    }
                )
    finally:
        torch.set_num_threads(previous[0])
        torch.use_deterministic_algorithms(previous[1], warn_only=previous[2])
    verify_bound_source_snapshot(root, snapshot, authorization["source_snapshot_sha256"])
    if (
        sha256_file(stage_path) != stage_sha
        or sha256_file(Path(__file__)) != source_sha
        or sha256_file(checkpoint) != stage["checkpoint_sha256"]
        or sha256_file(model) != stage["model_sha256"]
    ):
        raise ValueError("Verification evidence changed during execution")
    maximum = max(row["maximum_absolute_error"] for row in rows)
    report = {
        "schema": PARITY_SCHEMA,
        "stage": {"path": stage_path.relative_to(root).as_posix(), "sha256": stage_sha},
        "source_sha256": source_sha,
        "model_sha256": stage["model_sha256"],
        "checkpoint_sha256": stage["checkpoint_sha256"],
        "patch": asdict(patch),
        "feature_inventory_sha256": prepared.feature_inventory_sha256,
        "panel_count": len(rows),
        "train_panel_count": sum(row["split"] == "train" for row in rows),
        "development_panel_count": sum(row["split"] == "validation" for row in rows),
        "panels": rows,
        "maximum_absolute_error": maximum,
        "tolerance": TOLERANCE,
        "passed": maximum <= TOLERANCE,
        "torch_threads": 12,
        "onnx_threads": 1,
        "graph_optimization": "ORT_ENABLE_ALL",
        "optimizer_steps": 0,
        "private_reads": 0,
        "sealed_reads": 0,
        "production_approved": False,
        "elapsed_ms": (time.perf_counter() - started) * 1000,
    }
    (output / "report.json").write_bytes(canonical_json_bytes(report))
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("stage", "stage-sha", "output", "source-sha"):
        parser.add_argument("--" + name, required=True)
    args = parser.parse_args()
    report = verify(args.stage, args.stage_sha, args.output, args.source_sha)
    print(json.dumps({key: report[key] for key in ("passed", "maximum_absolute_error", "panel_count", "elapsed_ms")}))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
