# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Source-bound train-only runner for the isolated V42 OHEM objective."""

from __future__ import annotations

import argparse
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
import json
import math
from pathlib import Path
import time
from typing import Any, Callable, Mapping

from ml.markers import training_budget
from ml.markers.gate_seal import (
    sha256_file,
    source_bundle_sha256,
    verify_bound_source_snapshot,
)
from ml.ocr.inverse_db_target_v40 import train_p1 as v40
from ml.ocr.official_bakeoff import frozen_trunk_head, shape_ohem_head_training


REPO_ROOT = Path(__file__).resolve().parents[3]
ROOT = Path("ml/ocr/shape_ohem_db_head_v42")
CONFIG_PATH = ROOT / "training/p1.json"
PROTOCOL_PATH = ROOT / "protocol.json"
TASK = "ocr-detection"
REVISION = "graph-text-shape-ohem-db-head-v42"
CANDIDATE_ID = "P1"
CONFIG_SCHEMA = "graphreader.ocr-shape-ohem-db-head-v42-candidate-config.v1"
STAGE_SCHEMA = "graphreader.ocr-shape-ohem-db-head-v42-training-stage.v1"
BASE_V40_CONFIG_SHA256 = "c2b4bbd6a5c6d3703c0f8ef30015b922424e60c4e1fc653c244c818cf1474a1f"
BASE_V40_RESULT_SHA256 = "74d367fd5ed915cda431132d055184d370170d0157e902a05bc0a0b5c525f5aa"
OHEM_TRAINING_SOURCE_SHA256 = "1f0acc8975787995622b10fbc17d4c17a820e667548e6b21fea86728cf8f8b58"
CHECKPOINT_NAME = "selected-head.pt"
ONNX_NAME = "detector-shape-ohem-db-head-v42-p1.onnx"
STAGE_NAME = "training-stage.json"

RUNNER_SOURCE_PATHS = tuple(dict.fromkeys((
    *v40.RUNNER_SOURCE_PATHS,
    Path("ml/ocr/inverse_db_target_v40/P1_RESULT.json"),
    Path("ml/ocr/official_bakeoff/shape_ohem_head_training.py"),
    Path("ml/ocr/official_bakeoff/ohem_head_training.py"),
    Path("ml/ocr/ohem_db_head_v41/P1_RESULT.json"),
    Path("docs/GOAL-22-V41-RUNTIME-REJECTION-DIAGNOSIS.json"),
    ROOT / "__init__.py",
    ROOT / "train_p1.py",
    PROTOCOL_PATH,
)))

_CONFIG_KEYS = {
    "schema", "task", "revision", "candidate_id", "stage",
    "expected_runner_source_bundle_sha256", "base_v40_config", "base_v40_result",
    "shape_ohem_training_source", "recipe", "expected_data", "synthetic_only",
    "private_data", "sealed_data", "production_approval",
}
_RECIPE = {
    "seed": shape_ohem_head_training.SEED,
    "epochs": shape_ohem_head_training.EPOCHS,
    "batch_size": shape_ohem_head_training.BATCH_SIZE,
    "learning_rate": shape_ohem_head_training.LEARNING_RATE,
    "weight_decay": shape_ohem_head_training.WEIGHT_DECAY,
    "positive_objective": "equal_mean_dice_and_balanced_ohem_bce",
    "negative_ratio": shape_ohem_head_training.NEGATIVE_RATIO,
    "objective_identity": shape_ohem_head_training.OBJECTIVE_IDENTITY,
    "target_definition": "inverse_db_unclip_rectangle_v1",
    "supervision_mask": "all_ones_full_valid_runtime_crop",
    "empty_target_objective": "masked_mean_probability",
    "captured_parity_maximum_absolute_error": v40.v39.PARITY_TOLERANCE,
}
_EXPECTED_DATA = dict(v40._EXPECTED_DATA)


class V42TrainingError(RuntimeError):
    """The V42 authorization, dependency, objective, or output contract is invalid."""


@dataclass(frozen=True)
class PreparedTraining:
    config: Mapping[str, Any]
    config_path: Path
    config_sha256: str
    base: v40.PreparedTraining


@dataclass(frozen=True)
class TrainingDependencies:
    prepare_base: Callable[..., v40.PreparedTraining]
    extract_head: Callable[..., frozen_trunk_head.FrozenTrunkHeadBundle]
    train_head: Callable[..., shape_ohem_head_training.TrainingResult]
    patch_head: Callable[..., frozen_trunk_head.PatchResult]
    acquire: Callable[..., training_budget.TrainingAuthorization]
    verify_snapshot: Callable[[Path, Path, object], None]
    void: Callable[[training_budget.TrainingAuthorization, BaseException], Path]


def _default_dependencies() -> TrainingDependencies:
    return TrainingDependencies(
        v40.prepare_training,
        frozen_trunk_head.extract_frozen_trunk_head,
        shape_ohem_head_training.train_shape_ohem_head,
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
    """Authenticate V42 and reuse the exact V40 features and inverse targets."""

    root = repository_root.resolve()
    relative_config = v40._relative(root, config_path, "configuration")
    config, config_sha = _load_config(root / relative_config, root)
    expected_bundle = v40._sha(
        config["expected_runner_source_bundle_sha256"], "runner source bundle")
    if source_bundle_sha256(root, RUNNER_SOURCE_PATHS) != expected_bundle:
        raise V42TrainingError("V42 runner source bundle differs from the candidate configuration")

    base_config = _exact_descriptor(
        root, config["base_v40_config"], v40.CONFIG_PATH,
        BASE_V40_CONFIG_SHA256, "base V40 configuration")
    _exact_descriptor(
        root, config["base_v40_result"], Path("ml/ocr/inverse_db_target_v40/P1_RESULT.json"),
        BASE_V40_RESULT_SHA256, "base V40 outcome")
    _exact_descriptor(
        root, config["shape_ohem_training_source"],
        Path("ml/ocr/official_bakeoff/shape_ohem_head_training.py"),
        OHEM_TRAINING_SOURCE_SHA256, "OHEM training source")

    deps = dependencies or _default_dependencies()
    base = deps.prepare_base(base_config.relative_to(root), repository_root=root)
    if base.config_sha256 != BASE_V40_CONFIG_SHA256:
        raise V42TrainingError("Prepared V40 configuration identity changed")
    if len(base.training_panels) != _EXPECTED_DATA["train"]["panel_count"]:
        raise V42TrainingError("Prepared V40 training panel count changed")
    return PreparedTraining(config, relative_config, config_sha, base)


def train_candidate(
    output_dir: Path,
    config_path: Path = CONFIG_PATH,
    *,
    repository_root: Path = REPO_ROOT,
    dependencies: TrainingDependencies | None = None,
) -> dict[str, Any]:
    """Run one authorized train-only V42 stage; leave actual C# dev evaluation pending."""

    root = repository_root.resolve()
    output = (output_dir if output_dir.is_absolute() else root / output_dir).resolve()
    v40._require_inside(root, output, "output")
    if output.exists():
        raise V42TrainingError(f"V42 training output already exists: {output}")
    relative_config = v40._relative(root, config_path, "configuration")
    pre_config, _ = _load_config(root / relative_config, root)
    expected_bundle = v40._sha(
        pre_config["expected_runner_source_bundle_sha256"], "runner source bundle")
    if source_bundle_sha256(root, RUNNER_SOURCE_PATHS) != expected_bundle:
        raise V42TrainingError("V42 runner source bundle differs from the candidate configuration")

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
    result: shape_ohem_head_training.TrainingResult | None = None
    try:
        output.mkdir(parents=True)
        prepared = prepare_training(
            relative_config, repository_root=root, dependencies=deps)
        if authorization.binding.get("candidate_config_sha256") != prepared.config_sha256:
            raise V42TrainingError(
                "Training authorization configuration differs from prepared V42 bytes")
        if authorization.snapshot_path is None:
            raise V42TrainingError("Training authorization omitted its source snapshot")
        deps.verify_snapshot(
            root, authorization.snapshot_path,
            authorization.binding.get("source_snapshot_sha256"))
        parent_path = v40._bound_path(root, prepared.base.config["parent_model"], "parent model")
        bundle = deps.extract_head(parent_path, v40.PARENT_MODEL_SHA256)
        phase = "optimization"
        result = deps.train_head(bundle.head, prepared.base.training_panels)
        _validate_training_result(result, len(prepared.base.training_panels))
        phase = "artifacts"
        checkpoint_path = output / CHECKPOINT_NAME
        v40._write_checkpoint(checkpoint_path, bundle.head)
        onnx_path = output / ONNX_NAME
        patch = deps.patch_head(parent_path, onnx_path, bundle.head, v40.PARENT_MODEL_SHA256)
        _validate_patch(patch, onnx_path)
        deps.verify_snapshot(
            root, authorization.snapshot_path,
            authorization.binding.get("source_snapshot_sha256"))
        report = _success_report(
            root, prepared, authorization, result, patch,
            checkpoint_path, onnx_path, (time.perf_counter() - started) * 1000.0)
        v40._write_json(output / STAGE_NAME, report)
        return report
    except Exception as error:
        optimizer_steps = result.optimizer_steps if result is not None else None
        known = result is not None
        if phase == "preflight":
            optimizer_steps, known = 0, True
        failure = {
            "schema": STAGE_SCHEMA,
            "task": TASK,
            "revision": REVISION,
            "candidate_id": CANDIDATE_ID,
            "stage": "P1",
            "status": "failed_presealed",
            "phase": phase,
            "optimizer_steps": optimizer_steps,
            "optimizer_steps_known": known,
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
                v40._write_json(stage_path, failure)
        except Exception:
            pass
        finally:
            try:
                deps.void(authorization, error)
            except Exception:
                pass
        raise


def _validate_training_result(
    result: shape_ohem_head_training.TrainingResult,
    panel_count: int,
) -> None:
    fixed = (
        isinstance(result, shape_ohem_head_training.TrainingResult)
        and result.optimizer_steps == shape_ohem_head_training.EPOCHS * panel_count
        and result.epochs == shape_ohem_head_training.EPOCHS
        and result.panel_count == panel_count
        and result.positive_panel_count == panel_count
        and result.empty_positive_panels == 0
        and result.seed == shape_ohem_head_training.SEED
        and result.batch_size == shape_ohem_head_training.BATCH_SIZE
        and result.learning_rate == shape_ohem_head_training.LEARNING_RATE
        and result.weight_decay == shape_ohem_head_training.WEIGHT_DECAY
        and result.negative_ratio == shape_ohem_head_training.NEGATIVE_RATIO
        and result.objective_identity == shape_ohem_head_training.OBJECTIVE_IDENTITY
        and result.torch_backend == shape_ohem_head_training.TORCH_BACKEND
        and result.trainable_parameter_names == shape_ohem_head_training.TRAINABLE_PARAMETER_NAMES
        and result.frozen_batch_norm_sha256_before == result.frozen_batch_norm_sha256_after
        and 1 <= result.selected_epoch <= result.epochs
        and len(result.epoch_losses) == result.epochs
    )
    if not fixed:
        raise V42TrainingError("V42 training result differs from the fixed OHEM recipe")
    for index, row in enumerate(result.epoch_losses, start=1):
        values = (
            row.training_step_mean_loss, row.full_train_loss,
            row.full_positive_shape_ohem_loss, row.full_empty_negative_loss,
        )
        if (
            row.epoch != index
            or row.optimizer_steps != index * panel_count
            or row.full_positive_shape_ohem_loss is None
            or row.full_empty_negative_loss is not None
            or any(value is not None and isinstance(value, bool) for value in values)
            or any(value is not None and not isinstance(value, (int, float)) for value in values)
            or any(value is not None and not math.isfinite(value) for value in values)
            or any(value is not None and value < 0 for value in values)
            or not v40._is_sha(row.draw_order_sha256)
        ):
            raise V42TrainingError("V42 epoch evidence differs from the fixed OHEM recipe")
    minimum_epoch = min(
        result.epoch_losses,
        key=lambda row: row.full_train_loss,
    ).epoch
    if result.selected_epoch != minimum_epoch:
        raise V42TrainingError("V42 checkpoint is not the first minimum full-train loss")


def _validate_patch(patch: frozen_trunk_head.PatchResult, onnx_path: Path) -> None:
    if (
        tuple(patch.changed_constants) != tuple(frozen_trunk_head.TRAINABLE_CONSTANT_SHAPES)
        or tuple(patch.unchanged_trainable_constants)
        or patch.source_sha256 != v40.PARENT_MODEL_SHA256
        or patch.output_sha256 != sha256_file(onnx_path)
    ):
        raise V42TrainingError("V42 ONNX did not replace exactly the nine reviewed head constants")


def _success_report(
    root: Path,
    prepared: PreparedTraining,
    authorization: training_budget.TrainingAuthorization,
    result: shape_ohem_head_training.TrainingResult,
    patch: frozen_trunk_head.PatchResult,
    checkpoint_path: Path,
    onnx_path: Path,
    elapsed_ms: float,
) -> dict[str, Any]:
    base = prepared.base
    return {
        "schema": STAGE_SCHEMA,
        "task": TASK,
        "revision": REVISION,
        "candidate_id": CANDIDATE_ID,
        "stage": "P1",
        "status": "training_complete_pending_actual_csharp_dev_evaluation",
        "config_path": prepared.config_path.as_posix(),
        "config_sha256": prepared.config_sha256,
        "base_v40_config": dict(prepared.config["base_v40_config"]),
        "base_v40_result": dict(prepared.config["base_v40_result"]),
        "base_v40_config_sha256": base.config_sha256,
        "parent_model_sha256": v40.PARENT_MODEL_SHA256,
        "feature_inventory_sha256": base.base.feature_inventory_sha256,
        "captured_parity_maximum_absolute_error": base.base.parity_maximum_absolute_error,
        "captured_parity_tolerance": v40.v39.PARITY_TOLERANCE,
        "inverse_target_request": dict(base.config["inverse_target_request"]),
        "inverse_target_builder": dict(base.config["inverse_target_builder"]),
        "inverse_target_oracle_score": dict(base.config["inverse_target_oracle_score"]),
        "inverse_target_request_sha256": base.inverse_target_request_sha256,
        "inverse_target_inventory_sha256": base.target_inventory_sha256,
        "supervision_mask": {
            "definition": "all_ones_full_valid_runtime_crop",
            "rebuilt_pixel_count": base.rebuilt_mask_pixel_count,
            "previously_masked_pixel_count": base.previously_masked_pixel_count,
        },
        "objective_source": dict(prepared.config["shape_ohem_training_source"]),
        "train_source_count": base.base.train_source_count,
        "train_panel_count": len(base.training_panels),
        "train_full_source_truth_count": base.base.train_truth_count,
        "validation_source_count": base.base.validation_source_count,
        "validation_panel_count": base.base.validation_panel_count,
        "validation_full_source_truth_count": base.base.validation_truth_count,
        "training_result": asdict(result),
        "checkpoint": {
            "path": checkpoint_path.relative_to(root).as_posix(),
            "sha256": sha256_file(checkpoint_path),
            "content": "torch_state_dict_only",
        },
        "onnx": {
            "path": onnx_path.relative_to(root).as_posix(),
            "sha256": sha256_file(onnx_path),
            "source_sha256": patch.source_sha256,
            "changed_constants": list(patch.changed_constants),
            "unchanged_trainable_constants": list(patch.unchanged_trainable_constants),
        },
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
    config = v40._read_json_exact(path, None, "V42 configuration")
    if set(config) != _CONFIG_KEYS:
        raise V42TrainingError("V42 configuration fields changed")
    fixed = {
        "schema": CONFIG_SCHEMA,
        "task": TASK,
        "revision": REVISION,
        "candidate_id": CANDIDATE_ID,
        "stage": "P1",
        "synthetic_only": True,
        "private_data": False,
        "sealed_data": False,
        "production_approval": False,
    }
    if any(config.get(key) != value for key, value in fixed.items()):
        raise V42TrainingError("V42 configuration identity or scope changed")
    if config.get("recipe") != _RECIPE or config.get("expected_data") != _EXPECTED_DATA:
        raise V42TrainingError("V42 fixed recipe or full data inventory changed")
    v40._sha(config.get("expected_runner_source_bundle_sha256"), "runner source bundle")
    _exact_descriptor(
        root, config.get("base_v40_config"), v40.CONFIG_PATH,
        BASE_V40_CONFIG_SHA256, "base V40 configuration")
    _exact_descriptor(
        root, config.get("base_v40_result"), Path("ml/ocr/inverse_db_target_v40/P1_RESULT.json"),
        BASE_V40_RESULT_SHA256, "base V40 outcome")
    _exact_descriptor(
        root, config.get("shape_ohem_training_source"),
        Path("ml/ocr/official_bakeoff/shape_ohem_head_training.py"),
        OHEM_TRAINING_SOURCE_SHA256, "OHEM training source")
    return config, sha256_file(path)


def _exact_descriptor(
    root: Path,
    descriptor: Any,
    required_path: Path,
    required_sha: str,
    label: str,
) -> Path:
    try:
        return v40._exact_descriptor(root, descriptor, required_path, required_sha, label)
    except v40.V40TrainingError as error:
        raise V42TrainingError(str(error)) from error


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
    "CANDIDATE_ID", "CONFIG_PATH", "CONFIG_SCHEMA", "ONNX_NAME", "PreparedTraining",
    "REVISION", "RUNNER_SOURCE_PATHS", "STAGE_NAME", "STAGE_SCHEMA", "TASK",
    "TrainingDependencies", "V42TrainingError", "prepare_training", "train_candidate",
]
