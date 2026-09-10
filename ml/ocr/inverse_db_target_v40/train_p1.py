# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Source-bound train-only runner for the isolated V40 inverse DB target."""

from __future__ import annotations

import argparse
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from hashlib import sha256
from io import BytesIO
import json
import math
from pathlib import Path
import time
from typing import Any, Callable, Mapping, Sequence

import numpy as np
import torch

from ml.markers import training_budget
from ml.markers.gate_seal import (
    canonical_json_bytes,
    sha256_file,
    source_bundle_sha256,
    verify_bound_source_snapshot,
)
from ml.ocr.official_bakeoff import frozen_head_training, frozen_trunk_head
from ml.ocr.pretrained_db_head_v39 import train_p1 as v39


REPO_ROOT = Path(__file__).resolve().parents[3]
ROOT = Path("ml/ocr/inverse_db_target_v40")
CONFIG_PATH = ROOT / "training/p1.json"
PROTOCOL_PATH = ROOT / "protocol.json"
TASK = "ocr-detection"
REVISION = "graph-text-inverse-db-target-v40"
CANDIDATE_ID = "P1"
CONFIG_SCHEMA = "graphreader.ocr-inverse-db-target-v40-candidate-config.v1"
STAGE_SCHEMA = "graphreader.ocr-inverse-db-target-v40-training-stage.v1"
PARENT_MODEL_SHA256 = v39.PARENT_MODEL_SHA256
BASE_V39_CONFIG_SHA256 = "310de176c6ca08ad3b33ca4ad698e262a351b5a2f731c15746cf537e30c3f46d"
INVERSE_TARGET_REQUEST_SHA256 = "ee14d67f651e20ec9479fb9e59ce1ea1ef77542b057f3d62312c476685520b0b"
INVERSE_TARGET_BUILDER_SHA256 = "ec273483050ef573853be797e4df38df4c907339dc3b426a939522343ebaeb86"
INVERSE_TARGET_ORACLE_SCORE_SHA256 = "adf316d6298aea4d27bdd99c1807b8f125161036e839232fb835979173189476"
CHECKPOINT_NAME = "selected-head.pt"
ONNX_NAME = "detector-inverse-db-target-v40-p1.onnx"
STAGE_NAME = "training-stage.json"

RUNNER_SOURCE_PATHS = tuple(dict.fromkeys((
    *v39.RUNNER_SOURCE_PATHS,
    ROOT / "__init__.py",
    ROOT / "train_p1.py",
    PROTOCOL_PATH,
    Path("ml/ocr/official_bakeoff/db_inverse_targets.py"),
)))

_CONFIG_KEYS = {
    "schema", "task", "revision", "candidate_id", "stage",
    "expected_runner_source_bundle_sha256", "base_v39_config", "parent_model",
    "inverse_target_request", "inverse_target_builder", "inverse_target_oracle_score",
    "recipe", "expected_data", "synthetic_only", "private_data", "sealed_data",
    "production_approval",
}
_RECIPE = {
    "seed": frozen_head_training.SEED,
    "epochs": frozen_head_training.EPOCHS,
    "batch_size": frozen_head_training.BATCH_SIZE,
    "learning_rate": frozen_head_training.LEARNING_RATE,
    "weight_decay": frozen_head_training.WEIGHT_DECAY,
    "dice_epsilon": frozen_head_training.DICE_EPSILON,
    "target_definition": "inverse_db_unclip_rectangle_v1",
    "supervision_mask": "all_ones_full_valid_runtime_crop",
    "empty_target_objective": "masked_mean_probability",
    "captured_parity_maximum_absolute_error": v39.PARITY_TOLERANCE,
}
_EXPECTED_DATA = {
    "train": {"source_count": 20, "panel_count": 28, "full_source_truth_count": 709},
    "validation": {"source_count": 3, "panel_count": 9, "full_source_truth_count": 183},
}


class V40TrainingError(RuntimeError):
    """The V40 authorization, inverse target, or output contract is invalid."""


@dataclass(frozen=True)
class PreparedTraining:
    config: Mapping[str, Any]
    config_path: Path
    config_sha256: str
    base: v39.PreparedTraining
    training_panels: tuple[frozen_head_training.TrainingPanel, ...]
    target_inventory_sha256: str
    rebuilt_mask_pixel_count: int
    previously_masked_pixel_count: int
    inverse_target_request_sha256: str


@dataclass(frozen=True)
class TrainingDependencies:
    prepare_base: Callable[..., v39.PreparedTraining]
    extract_head: Callable[..., frozen_trunk_head.FrozenTrunkHeadBundle]
    train_head: Callable[..., frozen_head_training.TrainingResult]
    patch_head: Callable[..., frozen_trunk_head.PatchResult]
    acquire: Callable[..., training_budget.TrainingAuthorization]
    verify_snapshot: Callable[[Path, Path, object], None]
    void: Callable[[training_budget.TrainingAuthorization, BaseException], Path]


def _default_dependencies() -> TrainingDependencies:
    return TrainingDependencies(
        v39.prepare_training,
        frozen_trunk_head.extract_frozen_trunk_head,
        frozen_head_training.train_frozen_head,
        frozen_trunk_head.patch_head_constants,
        training_budget.acquire_training_candidate,
        verify_bound_source_snapshot,
        training_budget.void_candidate,
    )


def prepare_training(
    config_path: Path = CONFIG_PATH,
    *,
    repository_root: Path = REPO_ROOT,
    dependencies: TrainingDependencies | None = None,
) -> PreparedTraining:
    """Authenticate frozen V39 features and replace train labels only."""

    root = repository_root.resolve()
    relative_config = _relative(root, config_path, "configuration")
    config, config_sha = _load_config(root / relative_config, root)
    expected_bundle = _sha(config["expected_runner_source_bundle_sha256"], "runner source bundle")
    if source_bundle_sha256(root, RUNNER_SOURCE_PATHS) != expected_bundle:
        raise V40TrainingError("V40 runner source bundle differs from the authorized configuration")

    base_config = _exact_descriptor(
        root, config["base_v39_config"], v39.CONFIG_PATH, BASE_V39_CONFIG_SHA256,
        "base V39 configuration")
    parent_model = _exact_descriptor(
        root, config["parent_model"], None, PARENT_MODEL_SHA256, "parent model")
    builder = _exact_descriptor(
        root, config["inverse_target_builder"],
        Path("ml/ocr/official_bakeoff/db_inverse_targets.py"),
        INVERSE_TARGET_BUILDER_SHA256, "inverse target builder")
    request_path = _exact_descriptor(
        root, config["inverse_target_request"], None,
        INVERSE_TARGET_REQUEST_SHA256, "inverse target request")
    score_path = _exact_descriptor(
        root, config["inverse_target_oracle_score"], None,
        INVERSE_TARGET_ORACLE_SCORE_SHA256, "inverse target oracle score")

    deps = dependencies or _default_dependencies()
    base = deps.prepare_base(base_config.relative_to(root), repository_root=root)
    if base.config_sha256 != BASE_V39_CONFIG_SHA256:
        raise V40TrainingError("Prepared V39 base configuration identity changed")
    base_parent = _bound_path(root, base.config["parent_model"], "base parent model")
    if base_parent != parent_model or _sha(base.config["parent_model"]["sha256"], "base parent") != PARENT_MODEL_SHA256:
        raise V40TrainingError("V40 parent model differs from the frozen V39 initialization")

    request = _read_json_exact(request_path, INVERSE_TARGET_REQUEST_SHA256, "inverse target request")
    _validate_request_scope(request)
    _validate_request_evidence(root, request, base, builder)
    score = _read_json_exact(score_path, INVERSE_TARGET_ORACLE_SCORE_SHA256, "inverse target oracle score")
    _validate_oracle_score(root, score, request_path)
    panels, target_inventory, rebuilt_pixels, masked_pixels = _replace_training_targets(
        root, base.training_panels, request)
    return PreparedTraining(
        config,
        relative_config,
        config_sha,
        base,
        panels,
        target_inventory,
        rebuilt_pixels,
        masked_pixels,
        INVERSE_TARGET_REQUEST_SHA256,
    )


def train_candidate(
    output_dir: Path,
    config_path: Path = CONFIG_PATH,
    *,
    repository_root: Path = REPO_ROOT,
    dependencies: TrainingDependencies | None = None,
) -> dict[str, Any]:
    """Run one authorized train-only V40 stage and leave C# dev evaluation pending."""

    root = repository_root.resolve()
    output = (output_dir if output_dir.is_absolute() else root / output_dir).resolve()
    _require_inside(root, output, "output")
    if output.exists():
        raise V40TrainingError(f"V40 training output already exists: {output}")
    relative_config = _relative(root, config_path, "configuration")
    pre_config, _ = _load_config(root / relative_config, root)
    expected_bundle = _sha(pre_config["expected_runner_source_bundle_sha256"], "runner source bundle")
    if source_bundle_sha256(root, RUNNER_SOURCE_PATHS) != expected_bundle:
        raise V40TrainingError("V40 runner source bundle differs from the authorized configuration")

    deps = dependencies or _default_dependencies()
    authorization = deps.acquire(
        root,
        task=TASK,
        revision=REVISION,
        candidate_id=CANDIDATE_ID,
        config_path=relative_config,
        runner_source_paths=RUNNER_SOURCE_PATHS,
    )
    started = time.perf_counter()
    phase = "preflight"
    result: frozen_head_training.TrainingResult | None = None
    try:
        output.mkdir(parents=True)
        prepared = prepare_training(
            relative_config, repository_root=root, dependencies=deps)
        if authorization.snapshot_path is None:
            raise V40TrainingError("Training authorization omitted its source snapshot")
        deps.verify_snapshot(
            root, authorization.snapshot_path,
            authorization.binding.get("source_snapshot_sha256"))
        parent_path = _bound_path(root, prepared.config["parent_model"], "parent model")
        bundle = deps.extract_head(parent_path, PARENT_MODEL_SHA256)
        phase = "optimization"
        result = deps.train_head(bundle.head, prepared.training_panels)
        _validate_training_result(result, len(prepared.training_panels))
        phase = "artifacts"
        checkpoint_path = output / CHECKPOINT_NAME
        _write_checkpoint(checkpoint_path, bundle.head)
        onnx_path = output / ONNX_NAME
        patch = deps.patch_head(parent_path, onnx_path, bundle.head, PARENT_MODEL_SHA256)
        _validate_patch(patch, onnx_path)
        deps.verify_snapshot(
            root, authorization.snapshot_path,
            authorization.binding.get("source_snapshot_sha256"))
        report = _success_report(
            root, prepared, authorization, result, patch,
            checkpoint_path, onnx_path, (time.perf_counter() - started) * 1000.0)
        _write_json(output / STAGE_NAME, report)
        return report
    except Exception as error:
        if result is not None:
            optimizer_steps = result.optimizer_steps
            optimizer_steps_known = True
        elif phase == "preflight":
            optimizer_steps = 0
            optimizer_steps_known = True
        else:
            optimizer_steps = None
            optimizer_steps_known = False
        failure = {
            "schema": STAGE_SCHEMA,
            "task": TASK,
            "revision": REVISION,
            "candidate_id": CANDIDATE_ID,
            "stage": "P1",
            "status": "failed_presealed",
            "phase": phase,
            "optimizer_steps": optimizer_steps,
            "optimizer_steps_known": optimizer_steps_known,
            "exception_type": type(error).__name__,
            "exception_message": str(error),
            "completed_utc": datetime.now(timezone.utc).isoformat(),
            "private_data": False,
            "sealed_data": False,
            "sealed_runs": 0,
            "production_approval": False,
            "training_authorization": authorization.binding,
        }
        stage_path = output / STAGE_NAME
        try:
            if output.is_dir() and not stage_path.exists():
                _write_json(stage_path, failure)
        except Exception:
            # Preserve the stage failure while still attempting canonical void accounting.
            pass
        finally:
            try:
                deps.void(authorization, error)
            except Exception:
                # The originating failure remains the actionable cause.
                pass
        raise


def _replace_training_targets(
    root: Path,
    base_panels: Sequence[frozen_head_training.TrainingPanel],
    request: Mapping[str, Any],
) -> tuple[tuple[frozen_head_training.TrainingPanel, ...], str, int, int]:
    records = request.get("panels")
    if not isinstance(records, list) or len(records) != 37:
        raise V40TrainingError("Inverse target request must retain all 37 panels")
    train_records = [record for record in records if isinstance(record, dict) and record.get("split") == "train"]
    validation_records = [record for record in records if isinstance(record, dict) and record.get("split") == "validation"]
    if len(train_records) != 28 or len(validation_records) != 9 or len(train_records) + len(validation_records) != 37:
        raise V40TrainingError("Inverse target request must contain 28 train and 9 validation panels")
    base_ids = tuple(panel.panel_id for panel in base_panels)
    request_train_ids = tuple(_text(record.get("panel_id"), "target panel identity") for record in train_records)
    if request_train_ids != base_ids or len(set(request_train_ids)) != len(request_train_ids):
        raise V40TrainingError("Inverse train target order differs from the frozen V39 feature order")
    validation_ids = tuple(_text(record.get("panel_id"), "validation target identity") for record in validation_records)
    if len(set(validation_ids)) != 9 or set(validation_ids) & set(base_ids):
        raise V40TrainingError("Validation target inventory is duplicated or overlaps training")

    inventory = sha256()
    loaded: dict[str, np.ndarray] = {}
    projection_counts = {"train": 0, "validation": 0}
    for record in records:
        if not isinstance(record, dict) or set(record) != {
            "panel_id", "split", "width", "height", "tensor_width", "tensor_height",
            "target", "source_sha256", "panel_to_source_matrix", "projections",
        }:
            raise V40TrainingError("Inverse target panel fields changed")
        panel_id = _text(record.get("panel_id"), "target panel identity")
        split = _text(record.get("split"), "target split")
        if split not in projection_counts:
            raise V40TrainingError("Inverse target panel split changed")
        width = _positive_int(record.get("tensor_width"), "target tensor width")
        height = _positive_int(record.get("tensor_height"), "target tensor height")
        descriptor = record.get("target")
        target_path = _bound_path(root, descriptor, f"target {panel_id}")
        target_sha = _sha(descriptor.get("sha256") if isinstance(descriptor, dict) else None,
                          f"target {panel_id}")
        payload = _read_exact(target_path, target_sha, f"target {panel_id}")
        if len(payload) != width * height * 4:
            raise V40TrainingError(f"Inverse target byte count changed: {panel_id}")
        target = np.frombuffer(payload, dtype="<f4").reshape(1, height, width)
        if not np.isfinite(target).all() or not np.logical_or(target == 0, target == 1).all():
            raise V40TrainingError(f"Inverse target is not finite binary float32: {panel_id}")
        projections = record.get("projections")
        if not isinstance(projections, list):
            raise V40TrainingError(f"Inverse target projections are malformed: {panel_id}")
        seen_truth: set[str] = set()
        for projection in projections:
            if not isinstance(projection, dict) or set(projection) != {"truth_id", "panel_box"}:
                raise V40TrainingError(f"Inverse target projection fields changed: {panel_id}")
            truth_id = _sha(projection.get("truth_id"), "projection truth identity")
            if truth_id in seen_truth:
                raise V40TrainingError(f"Inverse target repeats a truth within one panel: {panel_id}")
            seen_truth.add(truth_id)
            _finite_box(projection.get("panel_box"), record, panel_id)
        projection_counts[split] += len(projections)
        inventory.update(panel_id.encode("utf-8") + b"\0")
        inventory.update(split.encode("ascii") + b"\0")
        inventory.update(target_sha.encode("ascii") + b"\n")
        if split == "train":
            loaded[panel_id] = target
    if projection_counts != {"train": 709, "validation": 183}:
        raise V40TrainingError("Inverse target package does not retain all 892 truth projections")

    rebuilt: list[frozen_head_training.TrainingPanel] = []
    rebuilt_pixels = 0
    masked_pixels = 0
    for panel in base_panels:
        target = loaded[panel.panel_id]
        if target.shape != panel.target.shape:
            raise V40TrainingError(f"Inverse target shape differs from frozen feature output: {panel.panel_id}")
        mask = np.frombuffer(
            np.ones(target.shape, dtype=np.float32).tobytes(), dtype=np.float32
        ).reshape(target.shape)
        masked_pixels += int(np.count_nonzero(np.asarray(panel.mask) == 0))
        rebuilt_pixels += int(mask.size)
        rebuilt.append(frozen_head_training.TrainingPanel(
            panel.panel_id, "train", panel.features, target, mask))
    if set(loaded) != set(base_ids) or not all(np.all(panel.mask == 1) for panel in rebuilt):
        raise V40TrainingError("V40 failed to rebuild complete train supervision")
    return tuple(rebuilt), inventory.hexdigest(), rebuilt_pixels, masked_pixels


def _validate_request_scope(request: Mapping[str, Any]) -> None:
    expected_keys = {
        "schema", "scope", "synthetic_only", "private_data", "sealed_data", "model_inference",
        "detector_manifest", "parent_model", "native", "exporter", "target_loader_sha256",
        "binding", "capture_report", "source_geometry_report", "synthetic_truth", "panels",
        "elapsed_ms", "claim_scope",
    }
    if set(request) != expected_keys or any((
        request.get("schema") != "graphreader.db-target-oracle-request.v1",
        request.get("scope") != "project-owned-synthetic-train-dev-target-diagnostic",
        request.get("synthetic_only") is not True,
        request.get("private_data") is not False,
        request.get("sealed_data") is not False,
        request.get("model_inference") is not False,
        request.get("target_loader_sha256") != INVERSE_TARGET_BUILDER_SHA256,
    )):
        raise V40TrainingError("Inverse target request scope or source identity changed")


def _validate_request_evidence(
    root: Path,
    request: Mapping[str, Any],
    base: v39.PreparedTraining,
    builder: Path,
) -> None:
    for key in (
        "detector_manifest", "parent_model", "native", "exporter", "binding",
        "capture_report", "source_geometry_report", "synthetic_truth",
    ):
        path = _bound_path(root, request.get(key), key)
        digest = _sha(request[key].get("sha256") if isinstance(request[key], dict) else None, key)
        _read_exact(path, digest, key)
    if _sha(request["parent_model"]["sha256"], "request parent model") != PARENT_MODEL_SHA256:
        raise V40TrainingError("Inverse target request parent model changed")
    if request["target_loader_sha256"] != sha256_file(builder):
        raise V40TrainingError("Inverse target request builder identity changed")
    for key, base_key in (("binding", "v3_binding"), ("capture_report", "capture_report")):
        expected_path = _bound_path(root, base.config[base_key], f"base {base_key}")
        observed_path = _bound_path(root, request[key], f"request {key}")
        if expected_path != observed_path or request[key]["sha256"] != base.config[base_key]["sha256"]:
            raise V40TrainingError(f"Inverse target request {key} differs from frozen V39 evidence")


def _validate_oracle_score(root: Path, score: Mapping[str, Any], request_path: Path) -> None:
    if any((
        score.get("schema") != "graphreader.db-target-oracle-score.v1",
        score.get("status") != "diagnostic_only",
        score.get("synthetic_only") is not True,
        score.get("private_data") is not False,
        score.get("sealed_data") is not False,
        score.get("model_inference") is not False,
        score.get("optimizer_steps") != 0,
        score.get("production_approval") is not False,
    )):
        raise V40TrainingError("Inverse target oracle score scope changed")
    request = score.get("request")
    if (_bound_path(root, request, "oracle score request") != request_path or
            _sha(request.get("sha256") if isinstance(request, dict) else None,
                 "oracle score request") != INVERSE_TARGET_REQUEST_SHA256):
        raise V40TrainingError("Inverse target oracle score request binding changed")
    report = score.get("report")
    report_path = _bound_path(root, report, "oracle report")
    report_sha = _sha(report.get("sha256") if isinstance(report, dict) else None, "oracle report")
    _read_exact(report_path, report_sha, "oracle report")
    denominators = score.get("denominators")
    expected = {
        "train": {"panel_count": 28, "truth_count": 709,
                  "degenerate_target_count": 0, "original_v39_degenerate_target_count": 16},
        "validation": {"panel_count": 9, "truth_count": 183,
                       "degenerate_target_count": 0, "original_v39_degenerate_target_count": 0},
    }
    if denominators != expected:
        raise V40TrainingError("Inverse target oracle full denominator changed")


def _validate_training_result(result: frozen_head_training.TrainingResult, panel_count: int) -> None:
    fixed = (
        isinstance(result, frozen_head_training.TrainingResult)
        and result.optimizer_steps == frozen_head_training.EPOCHS * panel_count
        and result.epochs == frozen_head_training.EPOCHS
        and result.panel_count == panel_count
        and result.seed == frozen_head_training.SEED
        and result.batch_size == frozen_head_training.BATCH_SIZE
        and result.learning_rate == frozen_head_training.LEARNING_RATE
        and result.weight_decay == frozen_head_training.WEIGHT_DECAY
        and result.dice_epsilon == frozen_head_training.DICE_EPSILON
        and result.torch_backend == frozen_head_training.TORCH_BACKEND
        and result.trainable_parameter_names == frozen_head_training.TRAINABLE_PARAMETER_NAMES
        and result.frozen_batch_norm_sha256_before == result.frozen_batch_norm_sha256_after
        and 1 <= result.selected_epoch <= result.epochs
        and len(result.epoch_losses) == result.epochs
    )
    if not fixed:
        raise V40TrainingError("V40 training result differs from the frozen V39 recipe")
    for index, row in enumerate(result.epoch_losses, start=1):
        values = (
            row.training_step_mean_loss, row.full_train_loss,
            row.full_positive_dice_loss, row.full_empty_negative_loss,
        )
        if (row.epoch != index or row.optimizer_steps != index * panel_count or
                any(value is not None and not math.isfinite(value) for value in values) or
                not _is_sha(row.draw_order_sha256)):
            raise V40TrainingError("V40 epoch evidence differs from the frozen recipe")


def _validate_patch(patch: frozen_trunk_head.PatchResult, onnx_path: Path) -> None:
    if (tuple(patch.changed_constants) != tuple(frozen_trunk_head.TRAINABLE_CONSTANT_SHAPES) or
            tuple(patch.unchanged_trainable_constants) or
            patch.source_sha256 != PARENT_MODEL_SHA256 or
            patch.output_sha256 != sha256_file(onnx_path)):
        raise V40TrainingError("V40 ONNX did not replace exactly the nine reviewed head constants")


def _success_report(
    root: Path,
    prepared: PreparedTraining,
    authorization: training_budget.TrainingAuthorization,
    result: frozen_head_training.TrainingResult,
    patch: frozen_trunk_head.PatchResult,
    checkpoint_path: Path,
    onnx_path: Path,
    elapsed_ms: float,
) -> dict[str, Any]:
    return {
        "schema": STAGE_SCHEMA,
        "task": TASK,
        "revision": REVISION,
        "candidate_id": CANDIDATE_ID,
        "stage": "P1",
        "status": "training_complete_pending_actual_csharp_dev_evaluation",
        "config_path": prepared.config_path.as_posix(),
        "config_sha256": prepared.config_sha256,
        "base_v39_config": dict(prepared.config["base_v39_config"]),
        "base_v39_config_sha256": prepared.base.config_sha256,
        "parent_model_sha256": PARENT_MODEL_SHA256,
        "feature_inventory_sha256": prepared.base.feature_inventory_sha256,
        "captured_parity_maximum_absolute_error": prepared.base.parity_maximum_absolute_error,
        "captured_parity_tolerance": v39.PARITY_TOLERANCE,
        "inverse_target_request": dict(prepared.config["inverse_target_request"]),
        "inverse_target_builder": dict(prepared.config["inverse_target_builder"]),
        "inverse_target_oracle_score": dict(prepared.config["inverse_target_oracle_score"]),
        "inverse_target_request_sha256": prepared.inverse_target_request_sha256,
        "inverse_target_inventory_sha256": prepared.target_inventory_sha256,
        "supervision_mask": {
            "definition": "all_ones_full_valid_runtime_crop",
            "rebuilt_pixel_count": prepared.rebuilt_mask_pixel_count,
            "previously_masked_pixel_count": prepared.previously_masked_pixel_count,
        },
        "train_source_count": prepared.base.train_source_count,
        "train_panel_count": len(prepared.training_panels),
        "train_full_source_truth_count": prepared.base.train_truth_count,
        "validation_source_count": prepared.base.validation_source_count,
        "validation_panel_count": prepared.base.validation_panel_count,
        "validation_full_source_truth_count": prepared.base.validation_truth_count,
        "training_result": asdict(result),
        "checkpoint": {"path": checkpoint_path.relative_to(root).as_posix(),
                       "sha256": sha256_file(checkpoint_path), "content": "torch_state_dict_only"},
        "onnx": {"path": onnx_path.relative_to(root).as_posix(),
                 "sha256": sha256_file(onnx_path), "source_sha256": patch.source_sha256,
                 "changed_constants": list(patch.changed_constants),
                 "unchanged_trainable_constants": list(patch.unchanged_trainable_constants)},
        "actual_csharp_dev_evaluation": "pending",
        "result_json_written": False,
        "canonical_ledger_updated": False,
        "synthetic_only": True,
        "private_data": False,
        "sealed_data": False,
        "sealed_runs": 0,
        "production_approval": False,
        "training_authorization": authorization.binding,
        "elapsed_ms": round(elapsed_ms, 3),
        "completed_utc": datetime.now(timezone.utc).isoformat(),
    }


def _load_config(path: Path, root: Path) -> tuple[dict[str, Any], str]:
    config = _read_json_exact(path, None, "V40 configuration")
    if set(config) != _CONFIG_KEYS:
        raise V40TrainingError("V40 configuration fields changed")
    fixed = {
        "schema": CONFIG_SCHEMA, "task": TASK, "revision": REVISION,
        "candidate_id": CANDIDATE_ID, "stage": "P1", "synthetic_only": True,
        "private_data": False, "sealed_data": False, "production_approval": False,
    }
    if any(config.get(key) != value for key, value in fixed.items()):
        raise V40TrainingError("V40 configuration identity or scope changed")
    if config.get("recipe") != _RECIPE or config.get("expected_data") != _EXPECTED_DATA:
        raise V40TrainingError("V40 fixed recipe or full data inventory changed")
    _sha(config.get("expected_runner_source_bundle_sha256"), "runner source bundle")
    bindings = (
        ("base_v39_config", v39.CONFIG_PATH, BASE_V39_CONFIG_SHA256),
        ("parent_model", None, PARENT_MODEL_SHA256),
        ("inverse_target_request", None, INVERSE_TARGET_REQUEST_SHA256),
        ("inverse_target_builder", Path("ml/ocr/official_bakeoff/db_inverse_targets.py"), INVERSE_TARGET_BUILDER_SHA256),
        ("inverse_target_oracle_score", None, INVERSE_TARGET_ORACLE_SCORE_SHA256),
    )
    for key, required_path, required_sha in bindings:
        _exact_descriptor(root, config.get(key), required_path, required_sha, key)
    return config, sha256_file(path)


def _exact_descriptor(
    root: Path,
    descriptor: Any,
    required_path: Path | None,
    required_sha: str,
    label: str,
) -> Path:
    path = _bound_path(root, descriptor, label)
    digest = _sha(descriptor.get("sha256") if isinstance(descriptor, dict) else None, label)
    if digest != required_sha or (required_path is not None and path != (root / required_path).resolve()):
        raise V40TrainingError(f"{label} differs from its fixed identity")
    _read_exact(path, digest, label)
    return path


def _finite_box(value: Any, panel: Mapping[str, Any], panel_id: str) -> None:
    if (not isinstance(value, list) or len(value) != 4 or
            any(isinstance(item, bool) or not isinstance(item, (int, float)) or
                not math.isfinite(float(item)) for item in value)):
        raise V40TrainingError(f"Inverse target projection box is malformed: {panel_id}")
    left, top, right, bottom = (float(item) for item in value)
    width = _positive_int(panel.get("width"), "panel width")
    height = _positive_int(panel.get("height"), "panel height")
    if left < 0 or top < 0 or right > width or bottom > height or right <= left or bottom <= top:
        raise V40TrainingError(f"Inverse target projection box escaped its panel: {panel_id}")


def _write_checkpoint(path: Path, head: frozen_trunk_head.FrozenDbHead) -> None:
    state = {name: value.detach().cpu() for name, value in head.state_dict().items()}
    buffer = BytesIO()
    torch.save(state, buffer)
    with path.open("xb") as stream:
        stream.write(buffer.getvalue())


def _write_json(path: Path, value: Mapping[str, Any]) -> None:
    with path.open("xb") as stream:
        stream.write(canonical_json_bytes(dict(value)))


def _read_json_exact(path: Path, expected_sha: str | None, label: str) -> dict[str, Any]:
    payload = _read_exact(path, expected_sha, label)
    try:
        value = json.loads(payload)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise V40TrainingError(f"{label} is not valid JSON") from error
    if not isinstance(value, dict):
        raise V40TrainingError(f"{label} must be an object")
    return value


def _read_exact(path: Path, expected_sha: str | None, label: str) -> bytes:
    try:
        payload = path.read_bytes()
    except OSError as error:
        raise V40TrainingError(f"Could not read {label}: {path}") from error
    if expected_sha is not None and sha256(payload).hexdigest() != expected_sha:
        raise V40TrainingError(f"{label} bytes differ from the authenticated identity")
    return payload


def _bound_path(root: Path, descriptor: Any, label: str) -> Path:
    if not isinstance(descriptor, dict) or set(descriptor) != {"path", "sha256"}:
        raise V40TrainingError(f"{label} descriptor changed")
    path_value = descriptor.get("path")
    if not isinstance(path_value, str) or not path_value.strip() or Path(path_value).is_absolute():
        raise V40TrainingError(f"{label} path must be repository-relative")
    path = (root / path_value).resolve()
    _require_inside(root, path, label)
    return path


def _relative(root: Path, path: Path, label: str) -> Path:
    candidate = path if path.is_absolute() else root / path
    resolved = candidate.resolve()
    _require_inside(root, resolved, label)
    return resolved.relative_to(root)


def _require_inside(root: Path, path: Path, label: str) -> None:
    if path == root or root not in path.parents:
        raise V40TrainingError(f"{label} escaped the repository")


def _sha(value: Any, label: str) -> str:
    if not isinstance(value, str) or not _is_sha(value):
        raise V40TrainingError(f"{label} SHA-256 must be lowercase hexadecimal")
    return value


def _is_sha(value: Any) -> bool:
    return (isinstance(value, str) and len(value) == 64 and value == value.lower() and
            all(character in "0123456789abcdef" for character in value))


def _text(value: Any, label: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise V40TrainingError(f"{label} must be non-empty text")
    return value


def _positive_int(value: Any, label: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value <= 0:
        raise V40TrainingError(f"{label} must be a positive integer")
    return value


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--config", type=Path, default=CONFIG_PATH)
    args = parser.parse_args()
    report = train_candidate(args.output, args.config)
    print(json.dumps({
        "status": report["status"],
        "optimizer_steps": report["training_result"]["optimizer_steps"],
        "actual_csharp_dev_evaluation": report["actual_csharp_dev_evaluation"],
    }, indent=2, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())


__all__ = [
    "CANDIDATE_ID", "CONFIG_PATH", "CONFIG_SCHEMA", "ONNX_NAME", "PARENT_MODEL_SHA256",
    "PreparedTraining", "REVISION", "RUNNER_SOURCE_PATHS", "STAGE_NAME", "STAGE_SCHEMA",
    "TASK", "TrainingDependencies", "V40TrainingError", "prepare_training", "train_candidate",
]
